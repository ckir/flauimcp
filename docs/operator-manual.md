# FlaUI.Mcp Operator Manual

This manual covers installing, configuring, running, and auditing the `flaui-mcp` daemon.

## Requirements

- **OS:** Windows 10/11, x64. Windows 10 build 19041 (version 2004) or later is required for OCR features.
- **Runtime:** None. Released binaries are self-contained, single-file executables.
- **Session:** An interactive desktop session. The agent cannot drive headless or locked sessions.

## Install

The installer places `flaui-mcp.exe` into `%LOCALAPPDATA%\Programs\FlaUI.Mcp\` and configures available agents.

### Standalone installer (recommended)

1. Download `flaui-mcp-setup.exe` from the latest release.
2. (Optional but recommended) Verify its SHA-256 against `SHA256SUMS.txt` from the same release.
3. Run it. Choose **More info → Run anyway** if SmartScreen warns (binaries are unsigned).
4. Restart your agent to load the new skill.

### Silent one-liner (PowerShell)

Run this to download and install silently:
```powershell
irm https://raw.githubusercontent.com/ckir/flauimcp/master/dist/install.ps1 | iex
```

### Manual install

Download `flaui-mcp.exe`, place it in a permanent directory, and run:
```powershell
flaui-mcp install --agent all
```

## Register with Claude Code

The installer generates a unified plugin (server config + `driving-flaui-mcp` skill) into a staging dir at `{app}\plugin`, then registers it with Claude Code by running `claude plugin marketplace add "<staging-dir>" --scope user` followed by `claude plugin install flaui-mcp@flaui-mcp-marketplace --scope user`. Claude references the staging dir in place — it does not copy it.

If you installed Claude Code *after* `flaui-mcp`, run:
```powershell
flaui-mcp install --agent claude
```
Restart Claude Code to load the plugin. Check registration status with `flaui-mcp status`.

## agy (Antigravity) parity

The installer registers the same staging dir with agy via `agy plugin install "<staging-dir>"`; agy copies the dir into its own managed plugins location. Restart agy to load it. Both agents share the identical driving skill versioned with the binary.

## CLI reference

| Command / Flag | Description |
|---|---|
| `flaui-mcp` | Start the stdio MCP server. |
| `--read-only-mode` | Start the server, blocking all state-changing tools. |
| `--unsafe-allow-elevation` | Allow synthetic input to elevated windows (default: hard-refused). |
| `--overlay` | Launch flag: enable red intent overlay before mutative actions. |
| `--overlay-ms=<ms>` | Launch flag: specify intent overlay duration in milliseconds. |
| `--autosound` | Launch flag: enable TTS spoken cue when target window needs attention. |
| `--presence` | Launch flag: enable coarse human presence sensing. |
| `--nearby-secs=<n>` | Launch flag: idle seconds before user is considered nearby. |
| `--away-secs=<n>` | Launch flag: idle seconds before user is considered away. |
| `--force-renderer-accessibility`| Target app launch flag: force Chromium to expose native accessibility trees. |
| `unlock --minutes N` | Grant a time-bounded synthetic-input lease. |
| `[--allow-shells]` | Pass to `unlock` to allow input to terminal/shell windows. |
| `[--accept-risk]` | Pass to `unlock` to suppress the interactive prompt for leases >60 minutes. |
| `[--i-understand]` | Pass to `unlock` as an alias for `--accept-risk`. |
| `lock` | Revoke the synthetic-input lease immediately. |
| `install --agent <name>` | Register the server (targets: `agy`, `generic`, `claude`, `all`). |
| `uninstall --agent <name>`| Unregister the server and revert configs. |
| `[--purge-data]` | Pass to `uninstall` to delete the MCP data directory. |
| `overlay on\|off` | Toggle the intent overlay. |
| `autosound on\|off` | Toggle the spoken attention cue. |
| `presence on\|off` | Toggle human presence sensing. |
| `print-config` | Print the JSON configuration snippet to stdout. |
| `status` | Print installation and registration status. |
| `--version` | Print the server version. |
| `--config <path>` | Override the target config file during install verbs. |

Use `--config <path>` with any command to override the target config file. Use `--help` for full syntax.

## Environment variables

| Variable | What it does | Default |
|---|---|---|
| `FLAUI_MCP_DATA_DIR` | Overrides the root directory for generic configs and presence states. | `%USERPROFILE%\.flaui-mcp` |
| `FLAUI_MCP_STATE_DIR` | Overrides the location for state files (e.g. uninstall warnings). | `%LOCALAPPDATA%\FlaUI.Mcp\state` |
| `FLAUI_MCP_STAGING_DIR` | Overrides the staging dir the installer generates the unified plugin into (server config + skill), which is then registered with both agents. | `{app}\plugin` |
| `FLAUI_MCP_AGY_PLUGINS_DIR` | Used only to locate and sweep the retired hand-written agy config on install/uninstall (legacy migration). Not the install target anymore. | `%USERPROFILE%\.gemini\config\plugins` |
| `FLAUI_MCP_CLAUDE_CONFIG_DIR` | Overrides the path for Claude Code's config/skills directory. | `%USERPROFILE%\.claude` |
| `CLAUDE_CONFIG_DIR` | Upstream Claude Code env var, honored as a fallback if `FLAUI_MCP_CLAUDE_CONFIG_DIR` is unset. | `%USERPROFILE%\.claude` |
| `FLAUI_MCP_REF_STRICT` | Ref-resolution mode for state-changing paths. Set `off` as a break-glass switch to force lenient resolution (disables the INV-8 identity guard) on apps whose UIA identity is too volatile for strict. | `strict` (unset = strict) |
| `FLAUI_MCP_REF_MAXSCOPES` | Tunes the ancestor fan-out cap for scope gathering. | `512` |
| `FLAUI_MCP_SELECTOR_MAXNODES` | Caps the number of nodes a selector walk evaluates. | `5000` |

## Grant and scope a lease

Synthetic input is **LOCKED** by default. You must explicitly unlock it by granting a time-boxed lease. See [`docs/architecture-and-safety.md`](architecture-and-safety.md) for the security rationale.

Grant a lease:
```powershell
flaui-mcp unlock --minutes 60
```
- **Over 60 minutes:** You must explicitly accept the risk interactively, or pass `--accept-risk` non-interactively.
- **Terminal shells:** Driving terminal windows requires an explicit opt-in:
  ```powershell
  flaui-mcp unlock --minutes 60 --allow-shells
  ```

Revoke a lease immediately:
```powershell
flaui-mcp lock
```

## Run in read-only mode

Run the server with `--read-only-mode` to block all destructive operations.

This mode blocks:
- Synthetic input (even if a lease is active).
- Pattern-based state changes (set value, toggle, expand, select).
- Destructive lifecycle actions (launch program, close window).

Read-only mode guarantees the agent can only perceive the desktop.

## Watch & audit the agent

Observe the agent's actions and target selections.

### Intent overlay
The overlay draws a red rectangle on the target element for ~500ms *before* any mutative action fires.
Enable it:
```powershell
flaui-mcp overlay on --agent all
```

### Audible attention cue
The cue speaks the target app name when a window needs foreground attention.
Enable it:
```powershell
flaui-mcp autosound on --agent all
```

### Human-presence sensing
`flaui-mcp presence on|off` enables a coarse, opt-in, human-only presence signal, OFF by default. The read-only `desktop_user_state` tool then returns `activity: active|nearby|away|null` (never raw idle time) so the agent can tell whether a human is at the keyboard. Optional thresholds: `--nearby-secs N` / `--away-secs N` (defaults 60 / 300; away must exceed nearby).

### Element-identity audit trace
Mutative actions leave an audit log entry. If a selector resolves an element, the trace records its stable identity (`RuntimeId`, `AutomationId`, `ClassName`, `ControlType`, `Bounds`). It strictly omits content-bearing properties like `Name` or `Value`.

### Auditing limitations
- The intent overlay is not visible in headless or RDP CI test runs.
- The audit trace currently only covers synthetic-input actions.
- `RuntimeId` is stable within a session, but changes if the target app restarts.
- The `overlay` and `autosound` flags do not merge non-destructively on the `claude` target; toggling one drops the other.

## What the installer changes

The installer writes NO agent MCP config file by hand. It generates the unified plugin into the staging dir, then registers it via each agent's own CLI, which owns writing its config.

| Target | Change |
|---|---|
| **Claude Code** | Registers via `claude plugin marketplace add`/`plugin install`, referencing the staging dir in place. Sweeps the retired `claude mcp` server + legacy `~/.claude/skills/flaui-mcp/` dir. Disables conflicting old marketplace plugins. |
| **Antigravity (agy)** | Registers via `agy plugin install "<staging-dir>"`, which agy copies into its own managed plugins dir. Sweeps the retired hand-written agy config. |
| **Generic MCP** | Writes the command snippet to `~/.flaui-mcp/generic-mcp.json`. |

## Uninstall

Uninstalling deregisters via each agent's CLI first (`claude plugin marketplace remove flaui-mcp-marketplace`, `agy plugin uninstall flaui-mcp`), then removes files and reverts configuration entries. It leaves unrelated settings untouched. The shared staging dir is deleted only on a full (`--agent all`) uninstall, and only once deregistration on both agents succeeded; if deregistration fails, the staging dir is left in place and a warning is surfaced via the uninstaller instead of guessing.

### Windows Settings
Uninstall "FlaUI.Mcp" from **Settings → Apps**. The uninstaller deregisters every agent via its CLI, re-enables any disabled plugins, and deletes the binaries.

### Manual uninstall
Run the CLI uninstaller, then delete the executable:
```powershell
flaui-mcp uninstall --agent all
```
