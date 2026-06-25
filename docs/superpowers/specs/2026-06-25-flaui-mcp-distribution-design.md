# FlaUI.Mcp — Distribution & Installation Design

**Date:** 2026-06-25
**Status:** Approved (design); amended after agy review
**Depends on:** Phase 1 (FlaUI.Mcp foundation, merged to `master` at `e7f7232`)

> **Amended 2026-06-25 (agy review, `gemini-code-1782385186187.md`):** the Claude Code
> plugin switched from **LFS-bundled exe** to the **Bootstrapper pattern** (marketplace
> installs do not fetch LFS objects → users would get a pointer file). Also folded in:
> JSONC-safe config writers, elevated emphasis on AV/SmartScreen, a UIPI cross-reference
> to Phase 1, and unification of both install paths on one binary location (kills state
> drift). UIPI/elevated-app handling is NOT new here — it is owned by Phase 1's
> `AccessDeniedIntegrity` + the ROADMAP elevated-app deferral.

## Goal

Make the FlaUI.Mcp Windows-desktop-automation MCP server trivial for users to
install into their agent of choice — **Claude Code**, **Antigravity (agy)**, and
**any generic MCP client** (Cursor, Cline, Claude Desktop) — with no prerequisites
(no .NET runtime, no build step) on the user's machine.

## Scope

- **In:** packaging the existing `FlaUI.Mcp.Server` as a self-contained single-file
  Windows executable; an "exe-is-its-own-installer" CLI; a universal PowerShell
  installer; a native Claude Code marketplace plugin (bootstrapper — fetches the exe); release
  CI that builds the exe and publishes GitHub Releases.
- **Out:** new server tools/features (those are Phases 2–6); code signing (v2);
  non-Windows targets (FlaUI/UIA is Windows-only); agents other than the three above
  (Codex etc. — v2).

## Decisions (locked during brainstorming)

| Decision | Choice |
| --- | --- |
| What we distribute | The FlaUI.Mcp server, multi-agent |
| Target agents (v1) | Claude Code, Antigravity (agy), Generic MCP (JSON) |
| Runtime artifact | Self-contained single-file `win-x64` exe (no prerequisites) |
| Install layers | **Both**: universal installer script **and** native Claude Code plugin |
| Claude Code plugin ↔ binary | **Bootstrapper pattern** — plugin ships a tiny launcher that ensures the exe exists in a shared location (fetches from GitHub Releases if missing/outdated), then runs it. No LFS, no binary in git, no CI back-commits. *(Revised from "bundle via LFS" after agy review: marketplace install does not resolve LFS objects.)* |
| Binary hosting | Public GitHub repo + GitHub Releases |
| Release process | CI builds + publishes releases on version tags |

## Architecture: the exe is its own installer

`flaui-mcp.exe` gains a thin CLI front; the default (no-args) path is unchanged.

```
flaui-mcp.exe                          # run the stdio MCP server (today's behavior)
flaui-mcp.exe install --agent <agy|generic|claude|all> [--scope user]
flaui-mcp.exe uninstall --agent <...>
flaui-mcp.exe print-config --agent <agy|generic|claude>   # emit JSON snippet to stdout
flaui-mcp.exe --version
```

`install` resolves the **exe's own absolute path** and writes each target agent's MCP
config so the agent launches *this* exe over stdio. All config-mutation logic lives in
one tested place (the exe), so the PowerShell installer, the plugin, and future agents
reuse it instead of duplicating JSON surgery.

**Arg dispatch:** `Program.cs` branches on `args[0]`: a known verb (`install`,
`uninstall`, `print-config`, `--version`/`-v`, `--help`) routes to the installer; any
other invocation (including no args) runs the MCP host exactly as today. This keeps the
stdio contract intact — MCP clients launch the exe with no args.

## Components

### 1. `flaui-mcp` CLI + per-agent config writers
New `Install/` namespace in `FlaUI.Mcp.Server`:

- **`CliRouter`** — parses `args`, dispatches to verbs or the host.
- **`AgentConfigWriter`** (one implementation per agent), each:
  - idempotent (re-running install does not duplicate entries);
  - backs up the target file before mutating (`<file>.bak-<timestamp>`);
  - detects and updates an existing `flaui-mcp` entry rather than appending;
  - **edits JSONC safely** — agy's and Cursor's settings files often contain comments and
    trailing commas. Parse with comments skipped and trailing commas allowed, preserve all
    unrelated keys, and never reformat-destroy the file. (A plain round-trip serializer
    would strip comments — flagged by agy as a HIGH corruption risk.) Write atomically
    (temp file + move) after the backup.
  - returns a structured result (created/updated/unchanged + path).

  Implementations:
  - **`AgyConfigWriter`** — Antigravity uses **Gemini-CLI-style** config. TWO edits are
    required:
    1. merge `mcpServers.flaui-mcp = { "command": "<exe path>", "args": [] }`
    2. append `"mcp(flaui-mcp/*)"` to `permissions.allow` (without it, agy blocks the
       server's tools).
    Target file: `~/.gemini/antigravity-cli/settings.json` (CLI-specific). The exact
    precedence vs `~/.gemini/settings.json` is verified at implementation against a live
    agy; the writer accepts a `--config <path>` override.
  - **`GenericMcpConfigWriter`** — emits the standard
    `{ "mcpServers": { "flaui-mcp": { "command": "<exe>", "args": [] } } }` snippet to
    stdout (`print-config`) and, given `--config <path>`, merges it into that file.
  - **`ClaudeCodeConfigWriter`** — non-plugin fallback for Claude Code users who don't
    use the plugin: shells `claude mcp add flaui-mcp -- "<exe>"` if the `claude` CLI is
    on PATH, else writes the user MCP config. (Primary Claude Code path is the plugin.)

### 2. Self-contained single-file publish
A publish profile / documented command:
```
dotnet publish src/FlaUI.Mcp.Server -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
Produces a single `flaui-mcp.exe` (assembly name set to `flaui-mcp`). The Server uses no
WPF/WinForms; FlaUI.UIA3 binds to OS COM (UI Automation) with no bundled native libs, so
single-file publish is clean. `<AssemblyName>flaui-mcp</AssemblyName>` and version
properties added to the Server csproj.

### 3. Claude Code plugin (`dist/claude-plugin/`) — Bootstrapper pattern
The plugin contains **no binary** (no LFS). It ships a tiny launcher script that resolves
or fetches the exe, so the plugin repo stays light and both install paths converge on one
binary.
```
dist/claude-plugin/
├─ .claude-plugin/plugin.json     # name "flaui-mcp", version, description, repository
├─ .mcp.json                      # mcpServers.flaui-mcp.command = the bootstrap launcher
└─ bin/flaui-mcp-launch.ps1       # bootstrapper (tiny, committed normally — no LFS)
```
`bin/flaui-mcp-launch.ps1` (the MCP server `command`):
1. Resolve the shared install path `%LOCALAPPDATA%\Programs\FlaUI.Mcp\flaui-mcp.exe`.
2. If missing or older than the plugin's pinned minimum version, download it from the
   matching GitHub Release to that path.
3. `exec` the exe with **no args** so it speaks stdio to the MCP client; the launcher
   passes stdin/stdout through transparently (no extra protocol framing).

`.mcp.json` invokes the launcher by a path relative to the plugin root. **Open item:**
confirm whether Claude Code interpolates `${CLAUDE_PLUGIN_ROOT}` in an MCP `command`
(agy's "refuted" was generic-MCP reasoning and did not account for Claude-Code-specific
behavior); if it does not, use the client's relative-to-`.mcp.json` path resolution.
Either way the launcher lives in-plugin, so there is no LFS dependency.

A root **`.claude-plugin/marketplace.json`** lists the plugin (local/url source) so users
run `claude plugin marketplace add <repo>` then `claude plugin install flaui-mcp`. Because
the plugin ships only a script, its version need not change on every exe release — it pins
a minimum exe version and self-heals.

### 4. Universal installer `dist/install.ps1`
One-liner target: `irm https://raw.githubusercontent.com/<owner>/<repo>/master/dist/install.ps1 | iex`.
- Downloads the latest (`-Version` to pin) `flaui-mcp.exe` from GitHub Releases to
  `%LOCALAPPDATA%\Programs\FlaUI.Mcp\flaui-mcp.exe`.
- Runs `flaui-mcp install --agent all` (configures agy + generic; Claude Code via the
  plugin, or `--agent claude` for the non-plugin fallback).
- Idempotent; `-Agent <agy|generic|claude|all>` selects targets; prints next steps.

### 5. Release CI `.github/workflows/release.yml`
Trigger: push of a `v*` tag.
1. Checkout, set up .NET 10.
2. Build + run **non-UIA** unit tests only (GitHub runners are headless — the
   window/UIA tests cannot run there; see Testing).
3. `dotnet publish` self-contained single-file → `flaui-mcp.exe`.
4. Create a GitHub Release for the tag; upload `flaui-mcp.exe` and `install.ps1` as assets.

CI's **only** outputs are the Release assets — there is **no back-commit to `master`** and
no LFS push. The Claude Code plugin ships only the launcher script, which fetches the
matching exe at runtime, so it needs no per-release update. (This removed the awkward
CI-pushes-to-default-branch cycle flagged in review.)

## Repo layout (monorepo — keeps the layers in sync)
```
aidesktop/
├─ src/, test/                       # existing server + tests
├─ src/FlaUI.Mcp.Server/Install/     # CliRouter + per-agent config writers
├─ dist/
│  ├─ install.ps1                    # universal installer (one-liner target)
│  └─ claude-plugin/                 # plugin; ships a launcher script, NO binary
│     ├─ .claude-plugin/plugin.json
│     ├─ .mcp.json
│     └─ bin/flaui-mcp-launch.ps1    # bootstrapper (fetches/locates the exe)
├─ .claude-plugin/marketplace.json   # marketplace entry (root)
└─ .github/workflows/release.yml     # release CI
```
*(No Git LFS / `.gitattributes` — the bootstrapper pattern keeps all binaries out of git.)*

## Testing

- **Unit (CI-safe, no real agents):** each `AgentConfigWriter` against temp dirs —
  fresh-create, idempotent re-run (no duplicate), existing-entry update, backup created,
  and the agy two-edit invariant (both `mcpServers` and `permissions.allow` touched).
  `print-config` snapshot tests. `CliRouter` dispatch tests (verb → installer; no-args /
  unknown → host path).
- **Trait split:** tag the existing window/UIA tests with a trait (e.g.
  `[Trait("Category","Desktop")]`) so CI can run `dotnet test --filter Category!=Desktop`.
  The desktop suite remains a manual / self-hosted interactive gate (as in Phase 1).
- **Manual integration (interactive, this machine):** publish the exe, run
  `flaui-mcp install --agent all`, and confirm the server loads in Claude Code (via the
  plugin) and agy; verify a `desktop_list_windows` call works end-to-end.
- **Release dry-run:** validate `release.yml` on a throwaway pre-release tag.

## Risks & open items

- **AV / SmartScreen (CRITICAL, agy)** — an unsigned, self-extracting .NET exe that
  *synthesizes keyboard/mouse input* is a strong AV/SmartScreen trigger and could
  materially hurt the "easy install" promise. v1 documents the SmartScreen "More info →
  Run anyway" step and publishes a checksum; **code signing is the top v2 item** (ROADMAP).
- **agy file precedence (open)** — config shape confirmed (Gemini-CLI-style `mcpServers` +
  `permissions.allow` `mcp(flaui-mcp/*)`); the exact file (`~/.gemini/antigravity-cli/
  settings.json` vs `~/.gemini/settings.json` vs an `mcp_config.json`) is verified at
  implementation against live agy. Writer takes a `--config` override. agy review left this
  UNKNOWN — confirm empirically before the agy writer is considered done.
- **`${CLAUDE_PLUGIN_ROOT}` / launcher path resolution (open)** — confirm how a plugin
  `.mcp.json` resolves its `command` path to the in-plugin launcher. Verify at
  implementation; do not rely on agy's generic-spec "refuted."
- **JSON corruption (resolved in design, agy HIGH)** — addressed by the JSONC-safe,
  content-preserving, atomic config writers (see Component 1).
- **State drift (resolved, agy MEDIUM)** — the bootstrapper and the universal installer
  both target the *same* `%LOCALAPPDATA%\Programs\FlaUI.Mcp\flaui-mcp.exe`, so there is one
  binary, not two.
- **UIPI / elevated apps (cross-ref, not new)** — a non-elevated server cannot drive
  elevated apps (UI Privilege Isolation). This is **already owned by Phase 1**: the
  `AccessDeniedIntegrity` error code surfaces it, and an elevated broker is a documented
  ROADMAP v2 deferral. Not a distribution-spec gap; the installer docs note it.
- **CI cannot run UIA tests** — headless GitHub runners; release CI runs build + non-UIA
  units only. Desktop tests stay a documented manual gate.
- **Single-file + COM/UIA** — expected clean (OS COM, no bundled natives); verified by
  smoke-running the published single-file exe before the first release.

## Success criteria

1. A user with neither .NET nor a build toolchain can install FlaUI.Mcp into Claude Code
   via the marketplace plugin and into agy via the one-liner installer, and drive the
   Windows desktop, with no manual config editing.
2. Re-running any install is idempotent and backs up touched config files.
3. A version tag produces a GitHub Release with the exe + installer; the Claude Code
   plugin's launcher fetches the matching exe to the shared location on first run (no
   binary in git, no CI back-commit).
