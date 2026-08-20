# FlaUI.Mcp Operator Manual

This manual covers installing, configuring, running, and auditing the `flaui-mcp` daemon.

## Requirements

- **OS:** Windows 10/11, x64. Windows 10 build 19041 (version 2004) or later is required for OCR features.
- **Runtime:** None. Released binaries are self-contained, single-file executables.
- **Session:** An interactive desktop session. The agent cannot drive headless or locked sessions.
- **Optional — `jq`:** only the autotrain nudge hook needs it. Without `jq` that hook silently never fires; everything else works. Install with `winget install jqlang.jq`.

## Install

The installer places `flaui-mcp.exe` into `%LOCALAPPDATA%\Programs\FlaUI.Mcp\` and configures available agents.

### Standalone installer (recommended)

1. Download `flaui-mcp-setup.exe` from the latest release.
2. (Optional but recommended) Verify its SHA-256 against `SHA256SUMS.txt` from the same release.
3. Run it. Choose **More info → Run anyway** if SmartScreen warns (binaries are unsigned).
4. Restart your agent to load the new skill — for Claude Code, quit the client completely and relaunch
   it, not just a new session.

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

The installer generates a unified plugin — server config, skills, hooks and scripts — into `{app}\plugin`, then registers it with Claude Code:
```powershell
claude plugin marketplace add "{app}\plugin" --scope user
claude plugin install flaui-mcp@flaui-mcp-marketplace --scope user
```
Claude **copies** the staging dir into a versioned cache and loads from there:
`~/.claude/plugins/cache/flaui-mcp-marketplace/flaui-mcp/<version>`.
Editing the staging dir by hand does nothing until you reinstall.

If you installed Claude Code *after* `flaui-mcp`, run:
```powershell
flaui-mcp install --agent claude
```

**Then quit Claude Code completely and relaunch it.** Not a new session — the client process itself.
Plugin hooks register only at client startup, so until you do, the activation hook is inert and the
desktop tools stay unannounced. Check registration with `flaui-mcp status`.

## Activation hook

A `SessionStart` hook injects a short reminder that the desktop tools exist, plus the one `ToolSearch`
call that loads them. It fires on `startup`, `clear` and `compact` — the moments an agent has just lost
its context. The hook only prints text; it touches nothing.

`status` reports its health:

```
Activation hook: wired (SessionStart -> flaui-mcp activation-payload) — Claude Code loads hooks only at client startup
```

`status` reads the staged file. It cannot see what the running client loaded, so `wired` means *we
wrote it correctly*, never *it is live in your session*. The trailing clause states the client's rule,
not a pending action — it keeps printing after you have restarted, because `status` has no way to tell.

| Reported | Meaning |
|---|---|
| `wired` | A SessionStart entry invokes the verb. Live in any client started since. |
| `not staged` | No plugin generated yet. Run `flaui-mcp install --agent claude`. |
| `staged but NOT wired` | Plugin exists, no SessionStart entry names the verb. Reinstall. |
| `staged but MALFORMED` | `hooks.json` has no top-level `hooks` object. Reinstall. |
| `staged but UNREADABLE` | File is locked or not valid JSON. |

The hook costs ~0.5 s and blocks the first turn — under 2% of a typical session start, and measured to
be below the noise floor end-to-end. If it ever fails to run, the session starts normally without it.

## agy (Antigravity) parity

Same plugin, registered with agy via `agy plugin install "{app}\plugin"` — agy copies it into its own managed plugins dir. Restart agy to load it. Both agents run the identical skill, versioned with the binary.

## CLI reference

| Command / Flag | Description |
|---|---|
| `flaui-mcp` | Start the stdio MCP server. |
| `--read-only-mode` | Start the server, blocking all state-changing tools. |
| `--redaction-rules <path>` | Start the server with operator redaction rules from a JSON file. Absent ⇒ the feature is off. See **Redaction rules** below. |
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
| `check-redaction-rules <path>` | Dry-run: validate a redaction-rule file and report whether a running server is enforcing it. Read-only; reads the file, never the desktop. |
| `[--list-windows]` | Pass to `check-redaction-rules` to list bindable windows as `pid` + title. |
| `[--window <pid>]` | Pass to `check-redaction-rules` to walk that **pid**'s window and print which elements the rules match. ⚠ Takes a **pid**, not a `wN` handle — handles are per-process and meaningless across two CLI invocations. |
| `print-config` | Print the JSON configuration snippet to stdout. |
| `status` | Print installation and registration status. |
| `activation-payload` | Print the SessionStart hook payload as JSON. Invoked by the hook, not by hand. |
| `--version`, `-v` | Print the server version. |
| `--config <path>` | Override the target config file during install verbs. |

Use `--config <path>` with any command to override the target config file. Use `--help`/`-h` for full syntax.

## Environment variables

| Variable | What it does | Default |
|---|---|---|
| `FLAUI_MCP_DATA_DIR` | Overrides the root directory for generic configs and presence states. | `%USERPROFILE%\.flaui-mcp` |
| `FLAUI_MCP_STATE_DIR` | Overrides the location for state files: uninstall warnings, and the [restore record](#conflicting-plugins-and-the-restore-record) for any plugin the installer disabled. | `%LOCALAPPDATA%\FlaUI.Mcp\state` |
| `FLAUI_MCP_STAGING_DIR` | Overrides the staging dir the installer generates the unified plugin into (server config + skill), which is then registered with both agents. | `{app}\plugin` |
| `FLAUI_MCP_AGY_PLUGINS_DIR` | agy's managed plugins dir. `status` reads it to report the deployed seed skill, and install/uninstall use it to sweep the retired hand-written agy config. Not the install *target* — `agy plugin install` chooses that itself. | `%USERPROFILE%\.gemini\config\plugins` |
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

## Redaction rules

Windows password fields are **always** redacted — no configuration, cannot be disabled. Redaction rules extend that to content the OS does not flag: an account number field, a customer name column, a licence key box.

Start the server with `--redaction-rules <path>`. Omit the flag and the feature is off.

```json
{
  "version": 1,
  "rules": [
    { "name": "acct-number", "processName": "Contoso.Billing", "automationId": "AccountNumber" },
    { "name": "ssn-cells",   "processName": "Contoso.Billing", "automationIdPattern": "^Cell_SSN_\\d+$" },
    { "name": "any-secret",  "global": true, "namePattern": "(?i)secret|token" }
  ]
}
```

| Field | Meaning |
|---|---|
| `name` | **Required.** Identifies the rule in diagnostics — and reaches the agent (see the warning below). |
| `processName` | Restrict to one process (no `.exe`, case-insensitive). **Required** unless `global` is true. |
| `global` | `true` ⇒ apply in every process. Use sparingly. ⚠ **Mutually exclusive with `processName`** — a rule setting both is rejected. |
| `automationId` | Exact automation-id match. |
| `automationIdPattern` | Regex over the automation id. |
| `namePattern` | Regex over the element's raw name. |

- `version` must be `1`.
- Rule names must be **unique** and non-empty.
- **Maximum 64 rules.** More than that is a policy problem, not a configuration one.
- Patterns compile as non-backtracking regexes, so a pathological pattern cannot hang the walk.
- An invalid file **refuses server startup** rather than starting unprotected. A file saved from Notepad with a UTF-8 BOM loads fine.

⚠ **Rule names reach the agent.** A redacted element reports `redactedBy: "rule:<name>"`, so the name itself is a disclosure. Name a rule for what it protects, not for the secret — `acme-prod-vault` is a poor rule name; `vault-field` is fine.

**Rules fail closed.** If an element's identity cannot be read, no rule can be evaluated against it — so its content is withheld and reported as `redactedBy: "unreadable"` rather than emitted unmatched. Seeing `"unreadable"` means the app did not answer a property read, not that you wrote a bad rule.

### Validate before you rely on it

```bash
flaui-mcp check-redaction-rules C:\path\rules.json
```

It always prints its results — it never withholds output to signal a problem. The exit code tells you what it found:

| Exit | Meaning |
|---|---|
| `0` | File is valid **and** a running server is enforcing this exact file (content hashes match). |
| `1` | File is missing or invalid. The message names the offending rule. |
| `3` | Valid, and a server is running — but on a **different** file. Restart the server to pick this one up. |
| `4` | Valid, a server is running, but enforcement cannot be determined (version skew, or its state file is unreadable). |
| `5` | Valid, no server running. |

To see what the rules actually match on a live window:

```bash
flaui-mcp check-redaction-rules rules.json --list-windows      # pids + titles
flaui-mcp check-redaction-rules rules.json --window 12345      # walk that pid
```

⚠ `--window` takes a **pid** from `--list-windows`, not a `wN` snapshot handle. A rule-matched element prints its **raw** name — you are debugging your own regex, and a redacted view would make that impossible. OS password fields stay `[REDACTED]` even here, and no element **value** is read on any path.

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

The installer hand-writes NO agent config. It generates the plugin; each agent's own CLI registers it and owns its config.

The generated plugin contains:

```
.mcp.json  plugin.json  .claude-plugin/marketplace.json
hooks/hooks.json                     SessionStart + Stop hooks
scripts/flaui-curate-nudge.sh        invoked by the Stop hook
skills/driving-flaui-mcp/SKILL.md    the driving manual
skills/flaui-learn/SKILL.md          capture a driving observation
skills/flaui-curate/SKILL.md         fold observations into the skill
```

`hooks.json` is **generated** at install time — its SessionStart command needs the installed exe's
absolute path. Everything else is extracted byte-for-byte from resources embedded in the binary.

| Target | Change |
|---|---|
| **Claude Code** | Registers via `claude plugin marketplace add`/`plugin install`; Claude copies the staging dir into its own versioned plugin cache. Sweeps the retired `claude mcp` server + legacy `~/.claude/skills/flaui-mcp/` dir. Disables conflicting marketplace copies and records them — see [Conflicting plugins](#conflicting-plugins-and-the-restore-record). |
| **Antigravity (agy)** | Registers via `agy plugin install "<staging-dir>"`, which agy copies into its own managed plugins dir. Sweeps the retired hand-written agy config. |
| **Generic MCP** | Writes the command snippet to `~/.flaui-mcp/generic-mcp.json`. |

## Conflicting plugins and the restore record

If you already installed `flaui-mcp` from your own marketplace, both copies ship a skill named
`driving-flaui-mcp` and the collision is silent. The installer **disables** your copy — never uninstalls
it — and records what it disabled at:

```
%LOCALAPPDATA%\FlaUI.Mcp\state\disabled-plugins.json
```

(the state dir, overridable with `FLAUI_MCP_STATE_DIR`.) That location is deliberate: it sits outside both
`{app}` and the data dir, so neither the uninstaller nor `--purge-data` destroys the record before it can
be used. It holds each entry's id, scope, project path, and the marketplace it came from.

Uninstall consumes the record and puts your copy back:

| Situation | What uninstall does |
|---|---|
| Copy still installed | Re-enables it at its own scope |
| Copy was removed by something else | Re-adds its marketplace, reinstalls it, then re-enables |
| That marketplace alias now points elsewhere | **Refuses**, and names both sources. An alias is a local name you control — installing blind would deliver software you did not ask for |
| No marketplace was recorded for it | Says so, and prints the exact command to put it back yourself |

The record is deleted once consumed, so a later uninstall cannot re-enable something you have since
disabled deliberately.

⚠ **Downgrading.** The record is schema `version: 2`. A **older** flaui-mcp cannot read it: it leaves the
file untouched, says so, and names every plugin still disabled plus the `claude plugin enable` command for
each. Running the newer build again fixes it completely. Nothing is lost — but the older build will not
restore for you.

## Uninstall

Uninstall deregisters via each agent's CLI first, then removes files:
```powershell
claude plugin marketplace remove flaui-mcp-marketplace
agy plugin uninstall flaui-mcp
```
The shared staging dir is deleted only on a full `--agent all` uninstall, and only if both agents deregistered cleanly. On failure it's left in place and the uninstaller warns. Unrelated settings are untouched.

### Windows Settings
Uninstall "FlaUI.Mcp" from **Settings → Apps**. The uninstaller deregisters every agent via its CLI, restores any plugin it disabled (see [Conflicting plugins](#conflicting-plugins-and-the-restore-record)), and deletes the binaries.

### Manual uninstall
Run the CLI uninstaller, then delete the executable:
```powershell
flaui-mcp uninstall --agent all
```
