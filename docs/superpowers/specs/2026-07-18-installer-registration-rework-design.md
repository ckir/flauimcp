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
The installer generates ONE plugin directory in the ISOLATED staging path `{localappdata}\Programs\FlaUI.Mcp\
plugin\` (under `{app}`, per Migration/Uninstall §§ — NOT the agent-managed `~/.gemini/config/plugins/`). That one
staging dir is BOTH the source `agy plugin install` copies from AND the Claude marketplace source dir. It
contains:
- `plugin.json` — metadata (name `flaui-mcp`, version, description). ALREADY deployed.
- `.mcp.json` — the MCP server declaration (NEW): `{ "mcpServers": { "flaui-mcp": { "command": "<abs exe
  path>", "args": [] } } }`. This is the single source of the server; read by both agents' plugin loaders.
  **Write it via `JsonSerializer`, NOT string interpolation (panel R4):** the abs exe path is a Windows path
  (`C:\Users\…\flaui-mcp.exe`) whose backslashes MUST be JSON-escaped (`C:\\Users\\…`); raw interpolation emits
  invalid escapes (`\U`, `\P`, …) and corrupts the manifest. Same for `marketplace.json`. (The live file today is
  correctly double-escaped — replicate that via a serializer, not by hand.)
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

**These files are NOT statically packaged — CliRouter.cs GENERATES them at runtime (panel R3, code-grounded).**
`installer/flaui-mcp.iss:25` `[Files]` ships ONLY `flaui-mcp.exe`; nothing else is in the setup payload. The
"ALREADY deployed" `plugin.json` + `skills/` above are deployed by the C# at RUNTIME (today: `ClaudeSkillDeployer`
writes the skill during `flaui-mcp install`), not by Inno. So the two NEW manifests (`.mcp.json`,
`.claude-plugin/marketplace.json`) must likewise be WRITTEN TO DISK by `CliRouter.cs` into the staging dir at
install time (extend the existing deployer, or add a plugin-staging step). If the spec's port assumes the files
are already on disk, the staging dir is empty and `agy plugin install`/`claude plugin marketplace add` load
nothing (silent false-success). Success criterion: after `install`, assert the staging dir actually contains all
four artifacts on disk.

**Exact current code paths to change in `CliRouter.cs.Apply()` (panel R3 — enumerate, don't hand-wave "stop
writing config"):** the install path today constructs `AgyConfigWriter` (`:249`/`:415`), `ClaudeCodeConfigWriter`
(`:257`/`:421`), `ClaudeSkillDeployer` (`:259`/`:270`), `ClaudeCollisionRemedy` (`:260`). The rework must specify,
per class: **RETIRE THE `Install()` (write) PATH, but REUSE `Uninstall()` AS THE MIGRATION SWEEP (panel R5)** for
`AgyConfigWriter` (its `Install()` hand-writes agy config — the root-cause bug — is dropped; its `Uninstall()`
`:250` already deletes the legacy agy `mcpServers` block and is exactly the Migration §'s cleanup — INVOKE it,
extended to cover BOTH `~/.gemini/settings.json` and `~/.gemini/config/mcp_config.json`, rather than rewrite the
deletion from scratch) and `ClaudeCodeConfigWriter` (drop its `Install()` `claude mcp add`; its `Uninstall()`
`:258` = `claude mcp remove flaui-mcp`, which IS the Claude-side migration sweep — invoke it). NOTE the
contradiction this resolves: "remove the classes" vs "the migration must delete the config those classes wrote"
— the deletion logic lives IN their `Uninstall()`, so keep that, retire only the write path; **RE-EVALUATE**
`ClaudeSkillDeployer` — **determinate, NOT a "decide later" (panel R4):** REPOINT it to write the skill into the
staging plugin dir (`{app}\plugin\skills\driving-flaui-mcp\`) so the skill ships INSIDE the plugin and both
`agy plugin install` and `claude plugin install` distribute it; DROP the separate legacy deploy to
`~/.claude/skills/flaui-mcp`. Leaving the deployer on the legacy path is a silent-break landmine: the staging dir
would lack `skills/`, `agy plugin install` copies an incomplete plugin, and agy gets the MCP server with NO
driving skill (Claude, still on the legacy path, would look fine — masking it). `ClaudeCollisionRemedy` —
**determinate ordering + self-exclusion (panel R5):** today `remedy.Apply()` runs AFTER registration (`:276`) and
disables an "incumbent colliding" marketplace copy; under the new model, if its matcher matches
`flaui-mcp-marketplace` it would disable the plugin we JUST registered (self-clobber). So (a) run its disable
BEFORE the new `claude plugin marketplace add` (as part of the pre-register migration sweep), and (b) its matcher
must EXCLUDE `flaui-mcp-marketplace` — only ever disable LEGACY/`skills-dir` copies, never our own new plugin.
Do NOT simply delete the class: its `Apply()`/`Restore()` pair is stateful (a disable-marker that uninstall's
`Restore()` `:288` re-enables) — preserve that disable/restore semantics wherever the logic lands.
This is NOT a blanket delete-all-four; the plan states the disposition of each.

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
  surfaces state). (On the running-guard, see Constraints — there is NO such guard in the current C# to "keep".)

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
+ "capture stdout/stderr" is satisfiable naively. Reuse the EXISTING `ProcessRunner.Run(file, string[] args, …)`
(`ProcessRunner.cs`) which already sets `UseShellExecute=false` + `RedirectStandardOutput/Error` (pipe capture)
and feeds args via `psi.ArgumentList`. Launch through `cmd.exe`: `Run("cmd.exe", ["/C", "claude", "plugin",
"install", "flaui-mcp@flaui-mcp-marketplace", "--scope", "user"], …)` — `cmd /C` resolves the `.cmd` shim via
PATH. **Two ArgumentList pitfalls (panel R4, code-grounded):** (1) pass the command as DISCRETE elements — never
one concatenated `"claude plugin install …"` string; a single element gets quoted whole and `cmd` treats the
quoted blob as the executable name and fails. `--scope user` is TWO elements (`"--scope","user"`). (2) Do NOT
copy clavity's `> "<tmp>" 2>&1` shell redirect — that is Inno-specific; through `ArgumentList` a `>` is a literal
argument, not a redirect. It is also UNNECESSARY: `ProcessRunner` already captures both streams via pipes. The
seam (`ICliRunner`) wraps this single orchestration.

**Agent-presence gating — the new agy CLI path needs it too (panel R5).** The claude branch already fails SOFT on
a missing CLI: `CliRouter.cs:269` `if (r.Change == AgentChange.NotFound) return r;` returns gracefully (skip that
agent, don't abort), so a single-agent machine installs fine today. But the OLD agy path only WROTE a file (no
CLI needed); the NEW `agy plugin install` path introduces a CLI dependency the old path lacked. So the agy branch
must gain the SAME NotFound gate: if `agy` is absent, skip + report "agy not found, skipped" — NEVER a fatal
abort, and NEVER a silent success. `--agent all` on an agy-only OR claude-only box must install the present agent
and cleanly skip the absent one. (This is the correct reading of "record a clear detail rather than silently
succeeding": it applies to a PRESENT-but-failing CLI, not an ABSENT agent, which is a legitimate skip.)
(NOTE: relying on the CLI is the design; a plugin-dir "drop + agent auto-discovery" — agy claims agy
auto-discovers a plugin dir dropped in `~/.gemini/config/plugins/` with no CLI call, Claude does not — is
UNVERIFIED agy confabulation-risk and is NOT relied upon here. Validate it during implementation; if true, it is
a bonus simplification for the agy path, not a design dependency.)

## Canonical ordered sequence (panel R6 — the single source of truth for step order)
Five rounds pinned individual step positions across different sections; consolidate them here so a plan-writer
follows ONE order, not scattered constraints. (Note: the migration sweep and the collision-remedy disable target
DIFFERENT things — `claude mcp remove`/settings-file deletion vs `claude plugin disable` of a legacy marketplace
copy — so their relative order is free; both simply precede registration.)

**INSTALL (per present agent; an absent agent is skipped+reported, never aborts):**
1. GENERATE the staging plugin dir `{app}\plugin\` — write `plugin.json`, `.mcp.json`, `.claude-plugin/
   marketplace.json` (via `JsonSerializer`), and deploy the skill into `{app}\plugin\skills\driving-flaui-mcp\`.
   (Fail the agent's install with a clear detail if generation fails — do not proceed to register an empty dir.)
2. LEGACY CLEANUP (all idempotent / swallow-absent): the migration sweep — `AgyConfigWriter.Uninstall()`
   (settings.json + mcp_config.json) and `ClaudeCodeConfigWriter.Uninstall()` (`claude mcp remove flaui-mcp`),
   remove stray `.mcp.json`; AND (Claude only) `ClaudeCollisionRemedy` disable of legacy/`skills-dir` copies
   (matcher EXCLUDES `flaui-mcp-marketplace`).
3. REGISTER — each is the FULL idempotent remove-then-add from the Registration mechanism § (do NOT drop the
   swallow-prefixes, or an upgrade collides): agy → `agy plugin uninstall flaui-mcp` (swallow) → `agy plugin
   install "<stagingDir>"`; Claude → `claude plugin marketplace remove flaui-mcp-marketplace` (swallow) → `claude
   plugin marketplace add "<stagingDir>" --scope user` → `claude plugin uninstall flaui-mcp` (swallow) → `claude
   plugin install "flaui-mcp@flaui-mcp-marketplace" --scope user` → read-back active-state. All CLI launches gated
   on the agent being present (NotFound ⇒ skip+report).

**UNINSTALL:**
1. DEREGISTER via CLIs (this removes the LOADED copies): `agy plugin uninstall flaui-mcp` (removes agy's MANAGED
   copy), `claude plugin uninstall flaui-mcp` + `claude plugin marketplace remove flaui-mcp-marketplace`,
   `ClaudeCollisionRemedy.Restore()` (re-enable what install disabled).
2. If deregister SUCCEEDED, delete the staged build artifact `{app}\plugin\`. If it FAILED, LEAVE files and emit
   a loud warning that NAMES BOTH the staged dir AND agy's managed copy (see Uninstall § two-copy note).

## Migration (existing installs)
**ORDER: the migration sweep runs BEFORE the new registration, never after (panel R3).** On the Claude side the
two are cross-namespace-safe (`claude mcp remove` touches the MCP-server list, not `claude plugin` registrations —
verified `ClaudeCodeConfigWriter.cs:7`), so order is only hygiene there. On the AGY side it is a real clobber
risk: `agy plugin install` copies the plugin into agy's managed plugins dir, which may BE `~/.gemini/config/
plugins/flaui-mcp/` — the exact path the sweep deletes to clean the stray hand-dropped `.mcp.json`. Sweep-after-
register would delete the freshly installed plugin. So on a re-install: sweep first, then register.

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
`claude plugin uninstall flaui-mcp`, `claude plugin marketplace remove flaui-mcp-marketplace` — and delete the
staged plugin dir ONLY if deregistration succeeded. If a CLI deregister FAILS, LEAVE the files and emit a loud
manual-cleanup warning: deleting a still-referenced marketplace/plugin dir leaves the agent pointing at a missing
dir → startup errors. "Fail-open" means uninstall still COMPLETES (never blocks); it does NOT mean delete-regardless.

**The two agents treat the staged dir ASYMMETRICALLY (panel R7 — corrects an R6 error; verify during impl).**
- **agy COPIES:** `agy plugin install "<stagingDir>"` copies the plugin into agy's MANAGED dir
  (`~/.gemini/config/plugins/flaui-mcp/`). After install, agy no longer needs `{app}\plugin\`; the copy agy LOADS
  is the managed one, removed by `agy plugin uninstall`.
- **Claude REFERENCES IN PLACE:** `claude plugin marketplace add "<stagingDir>"` registers a LOCAL marketplace
  pointed at that path — Claude loads LIVE from `{app}\plugin\` (this is why clavity leaves its marketplace/AppDir
  installed permanently and never deletes it). So `{app}\plugin\` is NOT a disposable build artifact for Claude:
  it MUST PERSIST as long as Claude is registered, and may be deleted only AFTER `claude plugin marketplace
  remove`. (The earlier "Claude resolves from its own registry, not the staged dir" was wrong.)

Consequences: (a) the canonical INSTALL correctly does NOT delete the staged dir — Claude needs it live. (b) On
uninstall, deregister BOTH agents FIRST (removes agy's managed copy AND drops Claude's live reference), THEN
delete `{app}\plugin\`. (c) On a deregister FAILURE, the manual-cleanup warning must (i) name agy's managed dir
`~/.gemini/config/plugins/flaui-mcp/` AND the staged `{app}\plugin\`, and (ii) instruct the operator to run the
CLI deregisters — `claude plugin marketplace remove flaui-mcp-marketplace`, `agy plugin uninstall flaui-mcp` —
BEFORE manually deleting either dir; deleting a still-referenced dir breaks the agent's startup (dangling
marketplace reference).

**The conditional delete lives in `CliRouter.cs`, NOT Inno (panel R3, code-grounded).** `installer/flaui-mcp.iss`
`[UninstallRun]:44-47` blindly invokes `flaui-mcp uninstall --agent all` and waits — Inno cannot see the per-CLI
exit codes gathered inside C#, so it CANNOT make the delete conditional. Therefore `CliRouter.cs`'s `uninstall`
verb must itself do the `Directory.Delete(stagingDir, recursive)` after a successful deregister. Note the staging
dir sits UNDER `{app}` (`{localappdata}\Programs\FlaUI.Mcp\plugin\`) but is runtime-generated, so it is NOT in
Inno's `[Files]` manifest — Inno will NOT remove it on its own; without the C# delete it is orphaned.

**Surface the fail-open warning via the EXISTING state-log, not stdout (panel R7).** `CliRouter.cs` runs headless
(`iss` `[UninstallRun]` `runhidden`) — a console warning is swallowed. Reuse the mechanism already in place: the
CLI writes cleanup warnings to `{localappdata}\FlaUI.Mcp\state\uninstall-warnings.log` (env-overridable
`FLAUI_MCP_STATE_DIR`), and `flaui-mcp.iss`'s `ShowUninstallWarnings()` (`:120-154`) reads that file and shows it
to the operator via `MsgBox` after uninstall. So the new fail-open manual-cleanup text (the two-copy CLI-first
instructions above) must be WRITTEN to that log, exactly as the current restore-warning path already does — do not
invent a new channel.

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
- Claude-running guard: there is NO such guard in the current `CliRouter.cs` (verified panel R3 — no process/
  running check anywhere in `src/FlaUI.Mcp.Server/Install/`; the earlier "keep the existing guard" was wrong).
  Decide during planning: the old `claude mcp` write-clobber concern is largely moot under `claude plugin` (the
  CLI mediates its own registry writes), so the likely answer is DROP it, not port it — but if a guard is wanted,
  port a `claude`-process check (clavity has one in `plugin-registration.iss`). Do NOT claim to "keep" a
  non-existent guard.
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
