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
  install flaui-mcp@flaui-mcp-marketplace` works. **Exact shape (pin it — panel R2):** because the staging dir
  IS the plugin dir (flat, per Migration §), `source` MUST be `"."`, NOT clavity's nested
  `"./plugins/<name>"`. Do NOT "mirror `marketplace.install.json`" (that file uses the nested layout for
  clavity's multi-plugin AppDir and would point Claude at a non-existent `plugins/flaui-mcp`). Mirror the
  FLAT variant (clavity's dev-clone `.claude-plugin/marketplace.json`, which uses `source: "."`):
  ```json
  { "$schema": "https://code.claude.com/schemas/marketplace.json",
    "name": "flaui-mcp-marketplace", "owner": { "name": "ckir" },
    "plugins": [ { "name": "flaui-mcp", "source": ".", "description": "…" } ] }
  ```
  **Pinned identifiers (invariant literals — panel R2):** plugin name = `flaui-mcp`; marketplace name =
  `flaui-mcp-marketplace`. These exact strings are reused verbatim across the `.mcp.json`, the marketplace
  manifest, every `claude plugin …` argv, and the read-back — never re-derived.

## Registration mechanism (replaces all hand-written config)
`CliRouter.cs` shells out to the agent CLIs (idempotent remove-then-add + read-back verify, porting clavity's
`plugin-registration.iss` logic to C#). It NO LONGER writes `mcpServers` into any config file.

**Argv grammar oracle (panel R2 — do NOT re-derive):** port the EXACT argv strings from clavity's proven
`installer/_shared/plugin-registration.iss` (`RegisterClaude`/`RegisterAgy`), which are live-verified. Note the
grammar: `--scope user` (a SPACE, not `--scope=user`), and the `@`-joined `<plugin>@<marketplace>` install
target quoted. The unit tests assert these exact strings (see Testing) so a grammar drift fails in CI, and the
strings are grounded in a working installer rather than authored fresh.
- **agy:** `agy plugin uninstall flaui-mcp` (swallow result) → `agy plugin install "<stagingDir>"` — where
  `<stagingDir>` is the ISOLATED staged dir, NOT `~/.gemini/config/plugins/`; `agy plugin install` copies it into
  its managed dir itself. Check exit code; capture stdout/stderr into the install-log detail.
- **Claude:** `claude plugin marketplace remove flaui-mcp-marketplace` (swallow) → `claude plugin marketplace
  add "<dir>" --scope user` → `claude plugin uninstall flaui-mcp` (swallow) → `claude plugin install
  "flaui-mcp@flaui-mcp-marketplace" --scope user` → read-back `claude plugin list` and assert the
  `flaui-mcp@flaui-mcp-marketplace` line is present AND not in a `Disabled`/`Error` state (panel R2: a bare
  substring "present" match is false-GREEN if the plugin lists but failed to load — clavity's oracle accepts the
  substring match, so this active-state check is a cheap hardening beyond it, applied only if `plugin list`
  surfaces state). The existing Claude-running-clobber guard stays: refuse if Claude Code is running.

### CLI resolution (install-context robustness)
The installer is PER-USER, non-elevated (`installer/flaui-mcp.iss:13` `PrivilegesRequired=lowest`; installs to
`{localappdata}`), so it runs as the actual user — there is NO UAC-elevation profile/PATH mismatch. (A panel R1
finding premised on elevated-shell `~`-misresolution is REFUTED by this measurement — agy's default worst-case-
elevation assumption; per-user install never triggers it.) Match clavity's PROVEN approach: invoke bare
`claude`/`agy` via a shell that inherits the user PATH (clavity does this and it works). If resolution fails,
fall back to known locations (`%APPDATA%\npm`, `~/.cargo/bin`, `~/.local/bin`).
On any launch failure, record a clear "could not launch <cli>" detail rather than silently succeeding.

**Exact launch orchestration (panel R2 — do NOT leave "via a shell" hand-wavy):** `claude`/`agy` are npm/cargo
shims (`claude.cmd`), which `CreateProcess` cannot launch by bare name, and `UseShellExecute=true` (the naive way
to get PATH/shim resolution) is MUTUALLY EXCLUSIVE with stream redirection in .NET (`RedirectStandardOutput`
throws `InvalidOperationException` when `UseShellExecute=true`). So neither half of "resolve the shim on PATH"
+ "capture stdout/stderr" is satisfiable naively. Port clavity's `ExecCaptured` shape
(`plugin-registration.iss:107`): run **`cmd.exe /C "<cli> <params>"`** with `UseShellExecute=false` and
`RedirectStandardOutput=true`/`RedirectStandardError=true` (or, matching clavity exactly, redirect to a temp file
`> "<tmp>" 2>&1` and read it back). `cmd /C` resolves the `.cmd` shim via PATH; `UseShellExecute=false` permits
the capture. This is the single supported orchestration — the seam (`ICliRunner`) encapsulates it.
(NOTE: relying on the CLI is the design; a plugin-dir "drop + agent auto-discovery" — agy claims agy
auto-discovers a plugin dir dropped in `~/.gemini/config/plugins/` with no CLI call, Claude does not — is
UNVERIFIED agy confabulation-risk and is NOT relied upon here. Validate it during implementation; if true, it is
a bonus simplification for the agy path, not a design dependency.)

## Migration (existing installs)
On install, sweep any LEGACY hand-written `flaui-mcp` entries so the old and new mechanisms don't collide
(the duplicate-server failure observed live). Delete a `flaui-mcp` `mcpServers` block from, if present:
`~/.gemini/settings.json`, `~/.gemini/config/mcp_config.json`, and the Claude side via exactly `claude mcp
remove flaui-mcp` **(swallow result — panel R2)**: a fresh machine has no legacy entry and `claude mcp remove`
exits non-zero for an absent server, so this step must NOT gate on its exit code (unlike a `RegisterClaude`
install failure) or it would fatally abort every clean install. (flaui-mcp's pre-rework Claude registration used
`claude mcp add`, so this is the precise inverse.) Also remove any stray hand-dropped `.mcp.json` the earlier
debugging left.
Idempotent: absent entries are a no-op.

## Uninstall
ORDER MATTERS (panel R1): deregister via the CLIs FIRST — `agy plugin uninstall flaui-mcp`,
`claude plugin uninstall flaui-mcp`, `claude plugin marketplace remove <marketplace>` — and delete the staged
plugin dir ONLY if deregistration succeeded. If a CLI deregister FAILS, LEAVE the files and emit a loud
manual-cleanup warning: deleting a still-referenced marketplace/plugin dir leaves the agent pointing at a missing
dir → startup errors. "Fail-open" means uninstall still COMPLETES (never blocks); it does NOT mean delete-regardless.

## Documentation update (IN SCOPE — user-added)
The operator-facing docs describe the OLD hand-written mechanism and MUST be rewritten to the plugin model in
the same change (they are wrong the moment the code lands). Concrete edits (verified against the files as they
exist now):
- `docs/operator-manual.md:36-48` — "Register with Claude Code" / agy sections: replace `claude mcp` +
  "static plugin under `%USERPROFILE%\.gemini\config\plugins\flaui-mcp\` … Restart agy" with the unified-plugin
  registration via `claude plugin marketplace add/install` + `agy plugin install`.
- `docs/operator-manual.md:155-162` ("What the installer changes") — the Antigravity row (line 162) documents
  appending to `~/.gemini/settings.json` **and a `antigravity-cli/settings.json` that does not exist** (a stale/
  fabricated path); replace both agent rows with "registers via the agent CLI; writes NO agent config file."
- `docs/operator-manual.md:88` — `FLAUI_MCP_AGY_PLUGINS_DIR` (overrides the agy plugins dir) is repurposed to
  the STAGING dir under `{localappdata}` (the installer no longer writes the agent-managed dir directly); update
  its description and default, or retire it if the staging dir is no longer user-overridable.
- `docs/operator-manual.md:165-175` (Uninstall) — reflect CLI-first deregistration + fail-open leave-and-warn.
- `README.md:18` — the quick-start "It configures Claude Code and Antigravity automatically" stays true but
  should not imply hand-written config; keep it accurate to the plugin model.
The doc edits are a task in the implementation plan, gated on the code change so citations don't drift. Verify
each line citation again at plan-authoring time (PLAN-vs-SPEC discipline) — line numbers may shift.

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
6. `docs/operator-manual.md` + `README.md` describe ONLY the plugin/CLI registration model — no lingering
   reference to hand-written `settings.json`/`mcp_config.json`, the drop-in plugins dir, or the fabricated
   `antigravity-cli/settings.json` path.

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
