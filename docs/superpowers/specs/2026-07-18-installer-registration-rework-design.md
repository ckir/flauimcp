# flaui-mcp installer registration rework — design

**Date:** 2026-07-18
**Status:** approved (goal + approach); pending user spec review
**Problem:** The flaui-mcp installer HAND-WRITES agent MCP config (it wrote the `flaui-mcp` server into
`~/.gemini/settings.json`). Antigravity migrated MCP config to `~/.gemini/config/mcp_config.json`, so the
registration was silently ignored and agy never loaded flaui-mcp. (A separate stdout-pollution bug, fixed in
v0.16.1, also blocked it.) The clavity installers avoid this by delegating to the agent CLIs
(`claude plugin marketplace add/install`, `agy plugin install "<dir>"`) — verified: clavity-dotnet is a plugin
with a `.mcp.json`, installed via those CLIs, and its MCP server loads in BOTH Claude and agy.

## Decision (settled — do not re-litigate)
- **Goal:** Rework flaui-mcp to register as a UNIFIED plugin (skill + `.mcp.json`) via the agent CLIs for BOTH
  agents, and STOP hand-writing any agent config file. Immune to config-path migrations. (User chose "robust
  rework now" + "unify both agents to the plugin model".)
- **Approach = A, self-contained C# port** (chosen over B "share clavity's Inno `.iss` machinery" — cross-repo
  coupling + untestable Pascal — and C "shared registrar tool" — premature/YAGNI). agy's divergent pass
  concurred with A. Reimplement clavity's `RegisterClaude`/`RegisterAgy` delegation inside
  `src/FlaUI.Mcp.Server/Install/CliRouter.cs` (C#, testable, self-contained).

## The unified plugin artifact
The installer deploys ONE plugin directory (target `~/.gemini/config/plugins/flaui-mcp/` for agy; the same
content is the Claude marketplace source dir) containing:
- `plugin.json` — metadata (name `flaui-mcp`, version, description). ALREADY deployed.
- `.mcp.json` — the MCP server declaration (NEW): `{ "mcpServers": { "flaui-mcp": { "command": "<abs exe
  path>", "args": [] } } }`. This is the single source of the server; read by both agents' plugin loaders.
- `skills/driving-flaui-mcp/` — the driving skill. ALREADY deployed.
- `.claude-plugin/marketplace.json` — a scoped Claude marketplace manifest (NEW) declaring this dir as a
  marketplace containing the `flaui-mcp` plugin, so `claude plugin marketplace add <dir>` + `claude plugin
  install flaui-mcp@<marketplace>` works. (Mirrors clavity's generated `marketplace.install.json`.)

## Registration mechanism (replaces all hand-written config)
`CliRouter.cs` shells out to the agent CLIs (idempotent remove-then-add + read-back verify, porting clavity's
`plugin-registration.iss` logic to C#). It NO LONGER writes `mcpServers` into any config file.

- **agy:** `agy plugin uninstall flaui-mcp` (swallow result) → `agy plugin install "<stagingDir>"` — where
  `<stagingDir>` is the ISOLATED staged dir, NOT `~/.gemini/config/plugins/`; `agy plugin install` copies it into
  its managed dir itself. Check exit code; capture stdout/stderr into the install-log detail.
- **Claude:** `claude plugin marketplace remove <marketplace>` (swallow) → `claude plugin marketplace add
  "<dir>" --scope user` → `claude plugin uninstall flaui-mcp` (swallow) → `claude plugin install
  "flaui-mcp@<marketplace>" --scope user` → read-back `claude plugin list` and assert `flaui-mcp@<marketplace>`
  is present (the existing Claude-running-clobber guard stays: refuse if Claude Code is running).

### CLI resolution (install-context robustness)
The installer is PER-USER, non-elevated (`installer/flaui-mcp.iss:13` `PrivilegesRequired=lowest`; installs to
`{localappdata}`), so it runs as the actual user — there is NO UAC-elevation profile/PATH mismatch. (A panel R1
finding premised on elevated-shell `~`-misresolution is REFUTED by this measurement — agy's default worst-case-
elevation assumption; per-user install never triggers it.) Match clavity's PROVEN approach: invoke bare
`claude`/`agy` via a shell that inherits the user PATH (clavity does this and it works). If resolution fails,
fall back to known locations (`%APPDATA%\npm`, `~/.cargo/bin`, `~/.local/bin`).
On any launch failure, record a clear "could not launch <cli>" detail rather than silently succeeding.
(NOTE: relying on the CLI is the design; a plugin-dir "drop + agent auto-discovery" — agy claims agy
auto-discovers a plugin dir dropped in `~/.gemini/config/plugins/` with no CLI call, Claude does not — is
UNVERIFIED agy confabulation-risk and is NOT relied upon here. Validate it during implementation; if true, it is
a bonus simplification for the agy path, not a design dependency.)

## Migration (existing installs)
On install, sweep any LEGACY hand-written `flaui-mcp` entries so the old and new mechanisms don't collide
(the duplicate-server failure observed live). Delete a `flaui-mcp` `mcpServers` block from, if present:
`~/.gemini/settings.json`, `~/.gemini/config/mcp_config.json`, and the Claude side via exactly `claude mcp
remove flaui-mcp` (flaui-mcp's pre-rework Claude registration used `claude mcp add`, so this is the precise
inverse). Also remove any stray hand-dropped `.mcp.json` the earlier debugging left.
Idempotent: absent entries are a no-op.

## Uninstall
ORDER MATTERS (panel R1): deregister via the CLIs FIRST — `agy plugin uninstall flaui-mcp`,
`claude plugin uninstall flaui-mcp`, `claude plugin marketplace remove <marketplace>` — and delete the staged
plugin dir ONLY if deregistration succeeded. If a CLI deregister FAILS, LEAVE the files and emit a loud
manual-cleanup warning: deleting a still-referenced marketplace/plugin dir leaves the agent pointing at a missing
dir → startup errors. "Fail-open" means uninstall still COMPLETES (never blocks); it does NOT mean delete-regardless.

## Bundled clavity fixes (verified in the installer review — in scope per "clavity repo is in scope")
1. `commonmemory/installer/commonmemory.iss:32` — add `.claude-plugin` to `Excludes` so `Source: "..\*"` stops
   shipping the dev-clone `marketplace.json` into the production plugin dir (nested-manifest violation; a risk
   agy itself introduced today). One-line fix.
2. `installer/_shared/plugin-registration.iss:91-96` (+ `137-154`) — `AgyPresent()`/`ClaudePresent()` return
   true when the config dir merely exists, discarding the resolved `FoundPath`; registration then invokes bare
   `agy`/`claude`. It FAILS GRACEFULLY (a Detail message, not the "crash" agy claimed). Optional low-severity
   hardening: use `FoundPath`, or require the CLI on PATH before attempting. Low priority.

## Success criteria (checkable)
1. A fresh install registers `desktop_*` tools in BOTH Claude and agy, and a grep of `~/.gemini/settings.json`
   + `~/.gemini/config/mcp_config.json` shows NO flaui-mcp `mcpServers` block written by the installer.
2. Survives an Antigravity MCP-config-path migration with no code change (delegation owns the path).
3. Uninstall removes flaui-mcp from both agents via the CLIs and deletes the plugin dir; never blocks on error.
4. A machine with a stale `settings.json` flaui-mcp entry is cleaned on the next install (no duplicate-server).
5. The installer's xUnit tests cover the new registration path (CLI invocation is seam-injected/faked so the
   tests stay headless — no real `claude`/`agy` required in CI).

## Constraints
- Never rename the literal flags `--read-only-mode` / `--allow-shells`.
- The `flaui-mcp` MCP server command/exe path is unchanged; only HOW it's registered changes.
- Keep the Claude-running clobber guard (refuse registration while Claude Code is running).
- Preserve the generic-MCP and stdout-fix behavior already shipped in 0.16.x.

## Testing
- Unit: fake the CLI-exec seam (an `ICliRunner` or equivalent) so `RegisterAgy`/`RegisterClaude`/uninstall/
  migration are tested headlessly (assert the exact CLI argv, the idempotent remove-then-add order, read-back
  parsing, fail-open on error, and the migration sweep on a fixture config). Category != Desktop.
- Manual (console): a real install on the dev box, then confirm both agents load `desktop_*` and neither
  config file was hand-written.

## Non-goals / follow-ons
- NOT adopting clavity's shared Inno machinery (Approach B) or a shared registrar tool (Approach C).
- NOT migrating clavity products' registration (they're already correct).
- The plugin-registration `FoundPath` hardening (clavity fix #2) is optional and may be deferred.
- Verifying the agy plugin-dir auto-discovery claim is an implementation-time task, not a design dependency.

## Implementation & review model
Per the session pattern: implementer writes to this spec; Claude reviews + verifies against source (agy
confabulates — every volunteered fact checked). CLI mechanism grounded in clavity's `plugin-registration.iss`.
Ships as flaui-mcp **0.16.2** (with the already-verified stdout fix + agy's interim path patch superseded by
this rework). Land on a feature branch; no push/tag without explicit user approval.
