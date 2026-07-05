[← Back to FlaUI.Mcp README](../README.md)

# Ops manual

Deployment and tooling reference: manual installation, what the installer changes, uninstalling,
and the full CLI. For the quick install (standalone installer / silent one-liner), see the
[README](../README.md#installation).

## Manual install (the exe is its own installer)

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

**`overlay` and `autosound` merge non-destructively for Antigravity and Generic MCP** — toggling one
flag on/off preserves whatever state the other flag was already in. **Known limitation:** the
**Claude Code** target is configured through the opaque `claude mcp` CLI, which cannot read back
existing args, so the two flags do **not** coexist there — toggling one re-registers with only that
flag and silently drops the other.

## Uninstall

- **Via the installer:** uninstall "FlaUI.Mcp" from **Settings → Apps** (or
  Add/Remove Programs). The uninstaller reverts every agent's config (targeted removal) and
  removes the files.
- **Manually:** `flaui-mcp uninstall --agent all`, then delete the executable.

Uninstalling reverts configuration entries but leaves your unrelated settings untouched.

## CLI reference

```text
flaui-mcp                                   # run the stdio MCP server (no args)
flaui-mcp --read-only-mode                  # run the server but refuse all state-changing tools
flaui-mcp --unsafe-allow-elevation          # allow synthetic input when running elevated (default: hard-refused)
flaui-mcp unlock --minutes N [--allow-shells] [--accept-risk]  # grant a time-bounded synthetic-input lease (human out-of-band); N>60 requires --accept-risk (alias --i-understand) or an interactive 'I understand' prompt — refused outright without a TTY and without the flag
flaui-mcp lock                              # revoke the synthetic-input lease immediately
flaui-mcp install   --agent agy|generic|claude|all
flaui-mcp uninstall --agent agy|generic|claude|all
flaui-mcp overlay on|off --agent agy|generic|claude|all     # toggle the intent overlay (re-registers with/without --overlay)
flaui-mcp autosound on|off --agent agy|generic|claude|all   # toggle the opt-in spoken attention cue (re-registers with/without --autosound); flash is always on regardless
flaui-mcp print-config --agent generic      # print the JSON snippet to stdout
flaui-mcp --version
flaui-mcp --help                            # structured help: all verbs, options, examples
```

`--config <path>` overrides the target config file (useful for testing without touching your
real agent configuration).
