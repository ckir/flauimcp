# install-removes-conflicting-marketplace-copy-and-never-restores-it — installing flaui-mcp permanently destroys a user's marketplace-installed copy

- **Captured:** 2026-08-20 (first real run of the v1.0 pre-release installer smoke)
- **Regression test:** `scripts/install-smoke.ps1` — the collision block. It is CORRECT and currently FAILS;
  do not "fix" the assertions to make it green.
- **Trait:** manual gate (`scripts/install-smoke.ps1`), not a `dotnet test` category — the check needs the
  real `claude` CLI and network access to seed a marketplace.

## Summary

`install --agent claude` prints:

> WARNING: disabled 1 conflicting marketplace copy/copies of the driving skill (they will be re-enabled if
> you uninstall flaui-mcp).

**Neither half of that sentence is true.** The conflicting copy is not disabled — it disappears from
`claude plugin list --json` entirely — and `uninstall --agent claude` then reports:

> WARNING: flaui-mcp@flaui-mcp (scope user) is no longer installed, so it was not re-enabled.

So a user who had installed flaui-mcp from the GitHub marketplace, then runs our installer, silently loses
that plugin and does not get it back by uninstalling. The tool promises a reversible action and performs an
irreversible one.

## Steps to Reproduce

MEASURED in an isolated `CLAUDE_CONFIG_DIR` sandbox, 2026-08-20, against `flaui-mcp 0.20.0`:

```powershell
$env:CLAUDE_CONFIG_DIR = <throwaway dir>; $env:FLAUI_MCP_STATE_DIR = <throwaway>; $env:FLAUI_MCP_DATA_DIR = <throwaway>
claude plugin marketplace add ckir/flauimcp
claude plugin install flaui-mcp@flaui-mcp --scope user
claude plugin list --json     # -> flaui-mcp@flaui-mcp, "enabled": true

.\publish\flaui-mcp.exe install --agent claude
#   -> "[claude] WARNING: disabled 1 conflicting marketplace copy/copies ..."
#   -> "Some targets did not complete - see <data>\install.log"
claude plugin list --json     # -> ONLY flaui-mcp@flaui-mcp-marketplace. The seeded copy is GONE,
                              #    not present with "enabled": false.

.\publish\flaui-mcp.exe uninstall --agent claude
#   -> "[claude] WARNING: flaui-mcp@flaui-mcp (scope user) is no longer installed, so it was not re-enabled."
```

Observed: `marketplace copy is DISABLED after install` FAILS and `marketplace copy is RESTORED after
uninstall` FAILS. `the marker was consumed` PASSES, which is consistent with the restore path running and
finding nothing to restore.

## Code-level Mitigation

The disable/restore path assumes the agent CLI's "disable" leaves the plugin **installed but disabled**, so
that recording its id in `disabled-plugins.json` is enough to re-enable it later. That assumption no longer
holds: the copy is deregistered, so re-enabling by id finds nothing.

Two candidate fixes, in preference order:

1. **Record enough to REINSTALL, not merely re-enable.** Persist the conflicting entry's `id`, `scope` and
   originating marketplace alongside the existing marker, and on uninstall reinstall it
   (`claude plugin install <id> --scope <scope>`) when a plain re-enable reports it absent. This keeps the
   promise the warning already makes.
2. **If reinstatement cannot be made reliable, stop promising it.** Change the warning to state plainly that
   the conflicting copy will be REMOVED and must be reinstalled manually, and name the id in the message so
   the user can. A truthful irreversible action beats a false reversible one.

Either way the `Some targets did not complete` line should name WHICH target failed — it currently sends the
reader to a log that, in the reproduction above, recorded only the summary line.

## Impact and scope

⚠ **Not a blocker for THIS machine's install:** the live config carries only
`flaui-mcp@flaui-mcp-marketplace` (our own local marketplace, 0.19.1), not a `flaui-mcp@flaui-mcp`
GitHub-marketplace copy, so there is nothing here for the defect to destroy.

⚠ **It IS a blocker for anyone who installed from the GitHub marketplace first**, which is a documented
install route. Severity is data-loss-shaped: silent, irreversible, and contradicted by the tool's own
message.

⚠ **The gate that catches this had never been run.** It is plan Task-12 Step-3, deferred at the installer
rework and still un-run when SP4 shipped. Its first execution also found the script itself asserting a
pre-rework layout (`<config>\skills\flaui-mcp\`) that no longer exists — fixed in the same session.
