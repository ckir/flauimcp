# FlaUI.Mcp — Distribution & Installation Design

**Date:** 2026-06-25
**Status:** Approved (design); spec under review
**Depends on:** Phase 1 (FlaUI.Mcp foundation, merged to `master` at `e7f7232`)

## Goal

Make the FlaUI.Mcp Windows-desktop-automation MCP server trivial for users to
install into their agent of choice — **Claude Code**, **Antigravity (agy)**, and
**any generic MCP client** (Cursor, Cline, Claude Desktop) — with no prerequisites
(no .NET runtime, no build step) on the user's machine.

## Scope

- **In:** packaging the existing `FlaUI.Mcp.Server` as a self-contained single-file
  Windows executable; an "exe-is-its-own-installer" CLI; a universal PowerShell
  installer; a native Claude Code marketplace plugin (with the exe bundled); release
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
| Claude Code plugin ↔ binary | **Bundle the exe** in the plugin (via Git LFS) — deterministic, offline, no runtime download |
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

### 3. Claude Code plugin (`dist/claude-plugin/`)
Bundles the exe via **Git LFS**:
```
dist/claude-plugin/
├─ .claude-plugin/plugin.json     # name "flaui-mcp", version, description, repository
├─ .mcp.json                      # mcpServers.flaui-mcp.command = ${CLAUDE_PLUGIN_ROOT}/bin/flaui-mcp.exe
└─ bin/flaui-mcp.exe              # LFS-tracked; refreshed by release CI
```
`.mcp.json`:
```json
{
  "mcpServers": {
    "flaui-mcp": {
      "command": "${CLAUDE_PLUGIN_ROOT}/bin/flaui-mcp.exe",
      "args": []
    }
  }
}
```
A root **`.claude-plugin/marketplace.json`** lists the plugin (local/url source) so users
run `claude plugin marketplace add <repo>` then `claude plugin install flaui-mcp`.

### 4. Universal installer `dist/install.ps1`
One-liner target: `irm https://raw.githubusercontent.com/<owner>/<repo>/master/dist/install.ps1 | iex`.
- Downloads the latest (`-Version` to pin) `flaui-mcp.exe` from GitHub Releases to
  `%LOCALAPPDATA%\Programs\FlaUI.Mcp\flaui-mcp.exe`.
- Runs `flaui-mcp install --agent all` (configures agy + generic; Claude Code via the
  plugin, or `--agent claude` for the non-plugin fallback).
- Idempotent; `-Agent <agy|generic|claude|all>` selects targets; prints next steps.

### 5. Release CI `.github/workflows/release.yml`
Trigger: push of a `v*` tag.
1. Checkout (with LFS), set up .NET 10.
2. Build + run **non-UIA** unit tests only (GitHub runners are headless — the
   window/UIA tests cannot run there; see Testing).
3. `dotnet publish` self-contained single-file → `flaui-mcp.exe`.
4. Create a GitHub Release for the tag; upload `flaui-mcp.exe` and `install.ps1` as assets.
5. Refresh the plugin's bundled `dist/claude-plugin/bin/flaui-mcp.exe` (LFS) and bump
   `plugin.json` version to match the tag; commit back to `master` (or attach a plugin
   zip asset) so the marketplace plugin ships the matching exe.

## Repo layout (monorepo — keeps the layers in sync)
```
aidesktop/
├─ src/, test/                       # existing server + tests
├─ src/FlaUI.Mcp.Server/Install/     # CliRouter + per-agent config writers
├─ dist/
│  ├─ install.ps1                    # universal installer (one-liner target)
│  └─ claude-plugin/                 # plugin; bin/flaui-mcp.exe via LFS
│     ├─ .claude-plugin/plugin.json
│     ├─ .mcp.json
│     └─ bin/flaui-mcp.exe
├─ .claude-plugin/marketplace.json   # marketplace entry (root)
├─ .github/workflows/release.yml     # release CI
└─ .gitattributes                    # *.exe filter=lfs
```

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

- **agy file precedence** — write target confirmed as Gemini-CLI-style `mcpServers` +
  `permissions.allow`; exact file (`antigravity-cli/settings.json` vs `.gemini/settings.json`)
  verified at implementation against live agy. Writer supports `--config` override.
- **Unsigned exe / SmartScreen** — a self-contained unsigned exe may trip SmartScreen on
  first run. v1 documents this; **code signing is a v2 item** (added to ROADMAP).
- **CI cannot run UIA tests** — headless GitHub runners; release CI runs build + non-UIA
  units only. Desktop tests stay a documented manual gate.
- **Git LFS bloat** — bundling the exe per version enlarges clones; accepted tradeoff for
  a deterministic, download-free plugin.
- **Single-file + COM/UIA** — expected clean (OS COM, no bundled natives); verified by
  smoke-running the published single-file exe before first release.

## Success criteria

1. A user with neither .NET nor a build toolchain can install FlaUI.Mcp into Claude Code
   via the marketplace plugin and into agy via the one-liner installer, and drive the
   Windows desktop, with no manual config editing.
2. Re-running any install is idempotent and backs up touched config files.
3. A version tag produces a GitHub Release with the exe + installer, and a plugin that
   bundles the matching exe.
