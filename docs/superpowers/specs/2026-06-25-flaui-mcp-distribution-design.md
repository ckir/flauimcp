# FlaUI.Mcp — Distribution & Installation Design

**Date:** 2026-06-25
**Status:** Approved (design); amended through two agy review rounds
**Depends on:** Phase 1 (FlaUI.Mcp foundation, merged to `master` at `e7f7232`)

## Amendment history
- **Initial:** Claude Code marketplace plugin bundling the exe via Git LFS + universal
  PowerShell installer.
- **agy round 1** (`gemini-code-1782385186187.md`): LFS bundling is unworkable (marketplace
  install does not fetch LFS objects) → switched to a launch-time **bootstrapper** launcher.
- **agy round 2** (`gemini-code-1782386161140.md`): a launch-time PowerShell launcher
  corrupts the MCP stdio stream (CRITICAL) and a mid-handshake download blows the
  `initialize` timeout (HIGH) → **abandoned the bootstrapper**. Replaced both the launcher
  and the PowerShell installer with a **standalone classic installer (Inno Setup)** that
  places the exe and configures agents up front; **the marketplace plugin is dropped**
  (the installer configures Claude Code directly). Also folded round-2 accepts: uninstall =
  targeted key removal (not backup-restore), update must stop the running process, agy
  requires a restart after config write.

## Goal

Make the FlaUI.Mcp Windows-desktop-automation MCP server trivial for users to install into
their agent of choice — **Claude Code**, **Antigravity (agy)**, and **any generic MCP
client** — with no prerequisites (no .NET runtime, no build step) and no manual config
editing.

## Scope

- **In:** the existing `FlaUI.Mcp.Server` published as a self-contained single-file Windows
  exe; an "exe-is-its-own-installer" CLI; per-agent config writers; a standalone Inno Setup
  installer that drops the exe, configures detected agents, and registers an uninstaller;
  an optional silent one-liner wrapper; release CI that builds the exe + installer and
  publishes GitHub Releases.
- **Out:** new server tools/features (Phases 2–6); code signing and MSIX/auto-update (v2);
  non-Windows targets (FlaUI/UIA is Windows-only); agents other than the three above.

## Decisions (locked through brainstorming + 2 review rounds)

| Decision | Choice |
| --- | --- |
| What we distribute | The FlaUI.Mcp server, multi-agent |
| Target agents (v1) | Claude Code, Antigravity (agy), Generic MCP (JSON) |
| Runtime artifact | Self-contained single-file `win-x64` exe (no prerequisites) |
| Primary installer | **Standalone classic installer (Inno Setup)** — `flaui-mcp-setup.exe` |
| MCP launch command | The **bare installed exe** (stdio-clean — no script/launcher wrapper) |
| Claude Code plugin | **Dropped** — the installer configures Claude Code directly |
| Binary delivery | Installer drops the exe at install time (no launch-time download) |
| Binary hosting | Public GitHub repo + GitHub Releases (exe + installer + checksums) |
| MSIX / auto-update / signing | **v2** (MSIX App Installer needs a trusted cert; pairs with signing) |

## Architecture

Two pieces, cleanly separated:

1. **`flaui-mcp.exe`** — the server *and* its own installer. No args → stdio MCP server
   (Phase 1 behavior, unchanged). Verb args → config management.
2. **`flaui-mcp-setup.exe`** (Inno Setup) — a thin OS installer that bundles the exe,
   places it, and drives `flaui-mcp install` as a post-install step; its uninstaller drives
   `flaui-mcp uninstall`.

Every agent launches the **bare installed exe** over stdio — no wrapper script, no
download in the MCP `initialize` handshake (both were round-2 CRITICAL/HIGH failures).

```
flaui-mcp.exe                          # run the stdio MCP server (no args)
flaui-mcp.exe install   --agent <agy|generic|claude|all>
flaui-mcp.exe uninstall --agent <agy|generic|claude|all>
flaui-mcp.exe print-config --agent <agy|generic|claude>   # JSON snippet to stdout
flaui-mcp.exe --version
```

**Arg dispatch:** `Program.cs` branches on `args[0]`: a known verb routes to the installer;
no args / unknown runs the MCP host exactly as today (keeps the stdio contract intact).

## Components

### 1. `flaui-mcp` CLI (`Install/` namespace in `FlaUI.Mcp.Server`)
- **`CliRouter`** — parses `args`, dispatches to a verb handler or the host.
- Resolves the exe's own absolute path so written configs point at *this* binary.

### 2. Per-agent config writers
One implementation per agent. Each is idempotent, **JSONC-safe** (parse with comments
skipped + trailing commas allowed; preserve all unrelated keys), writes **atomically**
(temp file + move) after a backup, and on uninstall performs **targeted key removal** —
never a blind backup-restore (restoring would wipe unrelated user edits made since install;
round-2 HIGH). Each returns a structured result (created/updated/unchanged/removed + path).

- **`AgyConfigWriter`** — Antigravity uses Gemini-CLI-style config; TWO edits:
  1. merge `mcpServers.flaui-mcp = { "command": "<exe path>", "args": [] }`
  2. append `"mcp(flaui-mcp/*)"` to `permissions.allow` (else agy blocks the tools).
  Empirical note: on the dev machine, `mcpServers` was observed in `~/.gemini/settings.json`
  while `permissions.allow` entries were in `~/.gemini/antigravity-cli/settings.json` — the
  two edits may target **different files**. agy round-2 claimed the base `~/.gemini/
  settings.json` with high confidence; this conflict is an **open item** resolved by
  inspecting a live agy at implementation. The writer takes per-file `--config` overrides.
  The installer/CLI prints "**restart agy** to re-init the tool registry" (round-2: required).
- **`GenericMcpConfigWriter`** — emits the standard
  `{ "mcpServers": { "flaui-mcp": { "command": "<exe>", "args": [] } } }` to stdout
  (`print-config`) and, given `--config <path>`, merges it into that file.
- **`ClaudeCodeConfigWriter`** — primary Claude Code path now (no plugin): run
  `claude mcp add flaui-mcp -- "<exe>"` if the `claude` CLI is on PATH, else write the user
  MCP config directly.

### 3. Self-contained single-file publish
```
dotnet publish src/FlaUI.Mcp.Server -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
`<AssemblyName>flaui-mcp</AssemblyName>` + version props in the Server csproj. The Server
uses no WPF/WinForms; FlaUI.UIA3 binds to OS COM (no bundled natives), so single-file is
clean — verified by smoke-running the published exe before first release.

### 4. Inno Setup installer `installer/flaui-mcp.iss` → `flaui-mcp-setup.exe`
- **Per-user install** (no admin) to `%LOCALAPPDATA%\Programs\FlaUI.Mcp\flaui-mcp.exe`;
  optionally add that dir to the user `PATH`.
- **Before install/upgrade:** stop any running `flaui-mcp.exe` (else the binary is locked;
  round-2 HIGH).
- **Post-install step:** run `flaui-mcp install --agent all` (configures agy + generic +
  Claude Code). Show the agy-restart note.
- **Uninstaller:** run `flaui-mcp uninstall --agent all` (targeted config removal), then
  remove files + PATH entry. Registered in Add/Remove Programs.
- Bundles the self-contained exe as its payload (produced by the publish step).

### 5. Optional silent one-liner (`dist/install.ps1`)
A thin convenience: `irm https://.../install.ps1 | iex` downloads `flaui-mcp-setup.exe` from
the latest Release (or `-Version`) and runs it `/VERYSILENT /SUPPRESSMSGBOXES`. Pure wrapper
over the installer — no config logic of its own.

### 6. Release CI `.github/workflows/release.yml`
Trigger: push of a `v*` tag.
1. Checkout, set up .NET 10.
2. Build + run **non-UIA** unit tests only (GitHub runners are headless; see Testing).
3. `dotnet publish` self-contained single-file → `flaui-mcp.exe`.
4. Build the Inno Setup installer → `flaui-mcp-setup.exe` (Inno via choco/winget on the runner).
5. Create a GitHub Release for the tag; upload `flaui-mcp-setup.exe`, `flaui-mcp.exe`,
   `install.ps1`, and SHA-256 checksums.

CI's only outputs are Release assets — **no LFS, no plugin, no back-commit to `master`.**

## Repo layout (monorepo)
```
aidesktop/
├─ src/, test/                       # existing server + tests
├─ src/FlaUI.Mcp.Server/Install/     # CliRouter + per-agent config writers
├─ installer/flaui-mcp.iss           # Inno Setup script
├─ dist/install.ps1                  # optional silent one-liner wrapper
└─ .github/workflows/release.yml     # release CI
```

## Testing

- **Unit (CI-safe, no real agents):** each config writer against temp dirs — fresh-create,
  idempotent re-run (no duplicate), existing-entry update, **targeted uninstall removal**
  (leaves unrelated keys intact), backup created, atomic write, JSONC preservation
  (comments/trailing commas survive), and the agy two-edit invariant. `print-config`
  snapshot tests. `CliRouter` dispatch tests (verb → installer; no-args/unknown → host).
- **Trait split:** tag the window/UIA tests `[Trait("Category","Desktop")]` so CI runs
  `dotnet test --filter Category!=Desktop`. The desktop suite stays a manual/self-hosted
  interactive gate (as in Phase 1).
- **Installer smoke (manual/interactive):** build `flaui-mcp-setup.exe`, run it
  `/VERYSILENT`, confirm the exe lands, agents are configured, the server loads in Claude
  Code + agy, a `desktop_list_windows` call works, then uninstall and confirm targeted
  config removal.
- **Release dry-run:** validate `release.yml` on a throwaway pre-release tag.

## Risks & open items

- **AV / SmartScreen (CRITICAL)** — an unsigned installer + an unsigned self-extracting exe
  that synthesizes input is a strong AV/SmartScreen trigger. v1 documents the "More info →
  Run anyway" step and ships SHA-256 checksums; **code signing + MSIX/auto-update is the top
  v2 item** (ROADMAP).
- **agy config-file precedence (open)** — confirm at implementation against live agy whether
  `mcpServers` and `permissions.allow` live in `~/.gemini/settings.json`,
  `~/.gemini/antigravity-cli/settings.json`, or split across both (on-disk evidence suggests
  a split; agy round-2 claimed the base file). Writer supports per-file overrides.
- **Update while running (handled)** — installer stops `flaui-mcp.exe` before replacing it.
- **Orphaned files on manual cleanup (low)** — the shared install is removed via the
  installer's uninstaller; document "uninstall via Add/Remove Programs (or `flaui-mcp
  uninstall --agent all`)" so configs are reverted, not just files deleted.
- **UIPI / elevated apps (cross-ref, not new)** — a non-elevated server can't drive elevated
  apps; already owned by Phase 1's `AccessDeniedIntegrity` + the ROADMAP elevated-broker
  deferral. Installer docs note it.
- **CI cannot run UIA tests** — headless runners; release CI runs build + non-UIA units only.

## Success criteria

1. A user with neither .NET nor a build toolchain runs one installer and ends up with
   FlaUI.Mcp configured in Claude Code, agy, and any generic MCP client, able to drive the
   Windows desktop — no manual config editing.
2. The MCP server launches as a bare stdio exe with no wrapper and no startup download.
3. Re-running install is idempotent; uninstall removes only FlaUI.Mcp's config entries
   (preserving unrelated user edits) and the installed files.
4. A version tag produces a GitHub Release with `flaui-mcp-setup.exe`, `flaui-mcp.exe`, the
   one-liner, and checksums — no LFS, no plugin, no CI back-commit.
