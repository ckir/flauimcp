# FlaUI.Mcp

A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server that lets an AI
agent — Claude Code, Antigravity (agy), or any MCP client — **control the Windows desktop**:
enumerate windows, launch applications, focus/close windows, and **snapshot a window's UI into
a ref-tagged accessibility tree**, with mouse/keyboard input synthesis on the roadmap. Think
"Playwright, but for native Windows apps."

---

## ⚠️ WARNING / DISCLAIMER — READ BEFORE INSTALLING

**Installing this software gives an AI agent the ability to operate your computer as if it
were sitting at the keyboard.** That is the entire point of the tool, and it is also the
entire risk. Understand the following before you install:

- **It runs with *your* user privileges.** Anything you can do without an admin prompt, the
  agent can do: launch programs, close windows (losing unsaved work), interact with your
  files, browser, email, and any open application. There is no built-in sandbox.
- **An agent can launch arbitrary executables.** The current tool surface already includes
  "launch this program with these arguments." A confused, jailbroken, or
  prompt-injected agent could start unwanted software. Treat the agent's permission to use
  this server as equivalent to giving it a shell.
- **Prompt injection is a real attack vector.** If the agent reads attacker-controlled
  content (a web page, a document, an email) that content can attempt to instruct the agent
  to take desktop actions. The blast radius of a successful injection is your whole desktop
  session.
- **The roadmap expands this surface.** Future versions add mouse/keyboard input synthesis
  and screen/accessibility-tree reading. The permission you grant at install
  (`mcp(flaui-mcp/*)`) covers those future tools too.
- **The released binaries are NOT code-signed.** Windows SmartScreen and antivirus software
  will likely flag the installer and the self-extracting executable. You will have to click
  through "More info → Run anyway." Verify the published SHA-256 checksums before trusting a
  download. (Code signing is a planned v2 improvement.)
- **It cannot drive elevated apps** (UIPI): a normally-launched server cannot send input to
  applications running as Administrator. This is a Windows security boundary, not a bug.
- **No warranty.** Per the license, the software is provided "as is," with no warranty and no
  liability for any damage arising from its use.

**Recommended use:** run it against a machine, VM, or user account where you are comfortable
with an automated agent taking actions on your behalf, and where you can supervise it. Do not
install it on a machine holding data you cannot afford to have an agent touch.

By installing, you accept these risks.

---

## What it does

FlaUI.Mcp is a stdio MCP server built on [FlaUI](https://github.com/FlaUI/FlaUI) (UI
Automation / UIA3) and the official MCP C# SDK. It exposes Windows desktop control as MCP
tools an agent can call.

**Current tools (window management + perception):**

| Tool | Read-only | Description |
| --- | --- | --- |
| `DesktopListWindows` | ✅ | List top-level windows with title, process name, and PID. |
| `DesktopOpenWindow` | ✅ | Open a window by `pid` or `title`, returning a handle (e.g. `w1`). |
| `DesktopSnapshot` | ✅ | Walk a window's UI into an indented, ref-tagged accessibility-tree snapshot. Each line carries an `eN` ref, control type, name, bounds, state, and supported patterns. Options: `interactiveOnly` (prune noise, default on), `fullProperties` (add AutomationId/HelpText), `includeOffscreen` (default off), `maxDepth`, and `root` (root the walk at a prior ref). |
| `DesktopLaunchApp` | — | Launch an executable (with optional args) and return a handle to its main window. |
| `DesktopFocusWindow` | — | Bring a window to the foreground. |
| `DesktopCloseWindow` | — | Close a window and free its handle. |

Read-only tools are annotated as such so MCP clients can auto-approve them while still
prompting for the mutating ones. Every tool returns structured JSON. Errors come back as a
uniform envelope (`{ error, message, suggestedRecovery }`) so the agent can recover rather than
crash the session.

### Perception safeguards (built in)

`DesktopSnapshot` reads UI into the agent's context, so it ships with privacy and safety floors
— defense in depth, not a substitute for supervising the agent:

- **Credential stores are blocked.** Snapshotting a window owned by a known password manager
  (1Password, Bitwarden, KeePass, and similar) is rejected outright (`TargetDenied`).
- **Password fields are always redacted.** Any UIA password field renders as `[REDACTED]`;
  typed secrets never enter a snapshot, even inside otherwise-allowed apps (e.g. a browser
  password box).
- **Off-screen elements are culled by default.** A snapshot reflects what the user can see; pass
  `includeOffscreen` to reach scrolled-off-but-real elements.
- **Never run elevated.** The server warns (on stderr) if started with Administrator rights — it
  is meant to run at your user integrity level.

**On the roadmap** (see [`ROADMAP.md`](ROADMAP.md)): screenshots / vision and pixel-precise
coordinates, mouse/keyboard input synthesis, structured patterns (grid/text/scroll), clipboard,
snapshot diff/stats, and an HTTP transport.

## Requirements

- **Windows 10/11, x64.** (FlaUI/UIA is Windows-only.)
- **No .NET runtime and no build tools required** for the released binaries — they are
  self-contained, single-file `win-x64` executables.
- An **interactive desktop session** (the agent drives real windows; it does not work
  headless).

## Installation

The installer drops `flaui-mcp.exe` into `%LOCALAPPDATA%\Programs\FlaUI.Mcp\` and configures
every agent it can find. No manual config editing is required.

### Option A — Standalone installer (recommended)

1. Download `flaui-mcp-setup.exe` from the [latest release](https://github.com/ckir/flauimcp/releases/latest).
2. (Optional but recommended) verify its SHA-256 against `SHA256SUMS.txt` from the same release.
3. Run it. SmartScreen will warn (unsigned) — choose **More info → Run anyway**.
4. The installer configures Claude Code, Antigravity, and a generic MCP config automatically.
5. **Restart Antigravity (agy)** if you use it, so it reloads its tool registry.

### Option B — Silent one-liner (PowerShell)

```powershell
irm https://raw.githubusercontent.com/ckir/flauimcp/master/dist/install.ps1 | iex
```

This downloads the latest `flaui-mcp-setup.exe` and runs it silently
(`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`). Pass `-Version vX.Y.Z` to pin a release.

### Option C — Manual (the exe is its own installer)

Download `flaui-mcp.exe` from the release, put it somewhere permanent, and run:

```powershell
flaui-mcp install --agent all
```

Use `--agent agy|generic|claude` to configure a single agent instead of all.

## What the installer changes

The configuration is **idempotent** (safe to re-run), writes **atomically** with a
timestamped backup of each file it touches, and on uninstall performs **targeted key
removal** — it only deletes FlaUI.Mcp's own entries and leaves your other settings intact.

| Agent | File(s) | Change |
| --- | --- | --- |
| **Claude Code** | (via `claude mcp add/remove` CLI) | Registers the `flaui-mcp` MCP server. |
| **Antigravity (agy)** | `~/.gemini/settings.json` + `~/.gemini/antigravity-cli/settings.json` | Adds `mcpServers.flaui-mcp` and appends `mcp(flaui-mcp/*)` to `permissions.allow`. |
| **Generic MCP** | `~/.flaui-mcp/generic-mcp.json` | Writes the standard `{ "mcpServers": { "flaui-mcp": { "command": "<exe>", "args": [] } } }` snippet. |

To see the generic config snippet without writing any file:

```powershell
flaui-mcp print-config --agent generic
```

## Usage

Once installed and your agent is restarted, just ask the agent to do desktop work — e.g.
"list my open windows," "launch Notepad," "focus the Calculator window." The agent calls the
MCP tools above.

The MCP server itself is the bare executable speaking JSON-RPC over stdio (no wrapper script,
no launch-time download). Running `flaui-mcp` with **no arguments** starts the server;
running it with a verb (`install`, `uninstall`, `print-config`, `--version`) runs the
installer instead.

## Uninstall

- **Via the installer:** uninstall "FlaUI.Mcp" from **Settings → Apps** (or
  Add/Remove Programs). The uninstaller reverts every agent's config (targeted removal) and
  removes the files.
- **Manually:** `flaui-mcp uninstall --agent all`, then delete the executable.

Uninstalling reverts configuration entries but leaves your unrelated settings untouched.

## CLI reference

```text
flaui-mcp                                   # run the stdio MCP server (no args)
flaui-mcp install   --agent agy|generic|claude|all
flaui-mcp uninstall --agent agy|generic|claude|all
flaui-mcp print-config --agent generic      # print the JSON snippet to stdout
flaui-mcp --version
```

`--config <path>` overrides the target config file (useful for testing without touching your
real agent configuration).

## Building from source

Requires the **.NET 10 SDK** (the project targets `net10.0-windows`).

```powershell
# Build + run tests (the UIA/window tests need an interactive desktop)
dotnet build test/FlaUI.Mcp.TestApp
dotnet test

# Run only the non-desktop unit tests (e.g. in headless CI)
dotnet test --filter "Category!=Desktop"

# Produce the self-contained single-file exe
dotnet publish src/FlaUI.Mcp.Server -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Tagging a commit `v*` triggers the release workflow
([`.github/workflows/release.yml`](.github/workflows/release.yml)), which builds the exe and
the Inno Setup installer and publishes them — with checksums — to a GitHub Release.

## License

[PolyForm Noncommercial License 1.0.0](LICENSE) — © 2026 Costas Kirgoussios. Free for
noncommercial use (personal, research, education, hobby, nonprofit, government). Commercial
use requires a separate license.
