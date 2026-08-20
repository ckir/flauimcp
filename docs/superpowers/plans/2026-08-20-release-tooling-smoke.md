# Installer smoke — acceptance evidence (release tooling / D1)

**Run:** 2026-08-20 · **exe built from:** `5578525` on `fix/installer-smoke-gate`
**`claude` CLI:** `2.1.237 (Claude Code)`
**Result: ALL CHECKS PASSED — 25/25.**

The smoke script itself was uncommitted at run time; it lands in the commit that carries this file.

## Why the CLI version is recorded

This gate depends on a third-party CLI, a real GitHub clone and the network. It cannot run offline and
cannot run in CI. A local fake marketplace would test the fake rather than the real CLI behaviour — and
the real CLI behaviour is exactly what D1 turned on. So a green result is only evidence about the CLI of
one particular day, and that day's version is stamped above.

## What this run proves that the previous gate did not

The two original collision checks — `DISABLED after install` and `RESTORED after uninstall` — go green
under fix (a) ALONE. Once the registrar's sweep is qualified, nothing evicts the copy, so the reinstall
fallback (layer b) would ship having never run once. Three new blocks close that:

- **the forcing block** simulates an eviction the qualified sweep no longer causes, AND removes the
  marketplace, so only the recorded SOURCE can rebuild the copy;
- **the repointed-alias block** pins the refusal that protects a user from a silently wrong install —
  it had no test anywhere before this;
- every new block carries an **"is able to run at all"** check, because a gate that could not run has
  not passed.

## MUTANT PROOF — the new checks are non-vacuous

With `TryReinstall` made to return `false` immediately (rebuilt and re-published), the same script gave:

```
PASS  marketplace copy is DISABLED after install         <- ORIGINAL check, still green
PASS  marketplace copy is RESTORED after uninstall       <- ORIGINAL check, still green
FAIL  the evicted copy was REINSTALLED from the recorded marketplace
FAIL  and re-enabled
FAIL  restore REFUSED to install from a repointed alias
3 CHECK(S) FAILED
```

That is the false-GREEN this task exists to close, demonstrated live: both original checks pass while
the entire reinstall path is dead. Reverting the mutant restored `ALL CHECKS PASSED`.

## Sandbox safety, verified before the run

The gate rests on `claude` honouring `CLAUDE_CONFIG_DIR` (set at `install-smoke.ps1:110`, torn down at
`:143`). MEASURED on the day: the operator's real config lists **17 plugins**, while
`CLAUDE_CONFIG_DIR=<tmp> claude plugin list --json` returns `[]`. Every plugin and marketplace mutation
below therefore landed in a temp directory. If that ever stops holding, this gate becomes the very
defect it tests for — re-run that two-line check before trusting it again.

## Full output

```

== install ==
[claude] Created: installed flaui-mcp@flaui-mcp-marketplace
If you configured agy, restart it to load the new tools.
If you configured Claude Code, quit the client completely and relaunch - it registers plugin hooks only at startup, so a new session is not enough.
Tip: to WATCH the agent act on screen, run  flaui-mcp overlay on  (off by default; flaui-mcp overlay off to disable), then reconnect. See the README's "Watching & auditing the agent" section.
  PASS  plugin payload deployed
  PASS  manifest deployed
  PASS  skill deployed
  PASS  mcp server declared
  PASS  hooks deployed

== version lockstep ==
  PASS  manifest 0.20.0 matches exe 0.20.0
  PASS  payload dir 0.20.0 matches exe 0.20.0

== the skill actually LOADS (validate would not catch this) ==
  PASS  our plugin is listed
  PASS  our plugin is enabled

== uninstall ==
[claude] Removed: claude plugin + marketplace removed
  PASS  plugin deregistered
  PASS  payload orphaned or gone

== collision: a seeded marketplace copy must end up DISABLED ==
claude CLI: 2.1.237 (Claude Code)
Adding marketplace…Cloning via SSH: git@github.com:ckir/flauimcp.git
Refreshing marketplace cache (timeout: 120s)…
Cloning repository (timeout: 120s): git@github.com:ckir/flauimcp.git
Clone complete, validating marketplace…
Cleaning up old marketplace cache…
✔ Successfully added marketplace: flaui-mcp (declared in user settings)
Installing plugin "flaui-mcp@flaui-mcp"...✔ Successfully installed plugin: flaui-mcp@flaui-mcp (scope: user)
  PASS  the marketplace copy could be seeded (the gate is able to run at all)
  PASS  seeded marketplace copy starts enabled
[claude] Created: installed flaui-mcp@flaui-mcp-marketplace
[claude] WARNING: disabled 1 conflicting marketplace copy/copies of the driving skill (they will be re-enabled if you uninstall flaui-mcp, and reinstalled first if anything has removed them).
Some targets did not complete - see C:\Users\user\AppData\Local\Temp\flaui-smoke2-535c04a2-ace6-4aa2-afd6-278b9dbd547f\data\install.log
If you configured agy, restart it to load the new tools.
If you configured Claude Code, quit the client completely and relaunch - it registers plugin hooks only at startup, so a new session is not enough.
Tip: to WATCH the agent act on screen, run  flaui-mcp overlay on  (off by default; flaui-mcp overlay off to disable), then reconnect. See the README's "Watching & auditing the agent" section.
  PASS  marketplace copy is DISABLED after install
[claude] Removed: claude plugin + marketplace removed
  PASS  marketplace copy is RESTORED after uninstall
  PASS  the marker was consumed

== collision: an EVICTED copy must be REINSTALLED on uninstall ==
Installing plugin "flaui-mcp@flaui-mcp"...✔ Plugin "flaui-mcp@flaui-mcp" is already installed (scope: user)
[claude] Created: installed flaui-mcp@flaui-mcp-marketplace
[claude] WARNING: disabled 1 conflicting marketplace copy/copies of the driving skill (they will be re-enabled if you uninstall flaui-mcp, and reinstalled first if anything has removed them).
Some targets did not complete - see C:\Users\user\AppData\Local\Temp\flaui-smoke2-535c04a2-ace6-4aa2-afd6-278b9dbd547f\data\install.log
If you configured agy, restart it to load the new tools.
If you configured Claude Code, quit the client completely and relaunch - it registers plugin hooks only at startup, so a new session is not enough.
Tip: to WATCH the agent act on screen, run  flaui-mcp overlay on  (off by default; flaui-mcp overlay off to disable), then reconnect. See the README's "Watching & auditing the agent" section.
  PASS  the reinstall scenario could be set up (the check is able to run at all)
  PASS  the marker is version 2
  PASS  the marker recorded the marketplace source
✔ Successfully uninstalled plugin: flaui-mcp (scope: user)
✔ Successfully removed marketplace: flaui-mcp
  PASS  the copy really was evicted (the precondition holds)
[claude] Removed: claude plugin + marketplace removed
[claude] WARNING: flaui-mcp@flaui-mcp (scope user) was no longer installed, so it was reinstalled from `flaui-mcp` (github: ckir/flauimcp), after re-adding that marketplace. could not re-enable flaui-mcp@flaui-mcp (scope user): claude exited 1. To restore it yourself: claude plugin enable flaui-mcp@flaui-mcp --scope user
Some targets did not complete - see C:\Users\user\AppData\Local\Temp\flaui-smoke2-535c04a2-ace6-4aa2-afd6-278b9dbd547f\data\install.log
  PASS  the evicted copy was REINSTALLED from the recorded marketplace
  PASS  and re-enabled

== collision: a REPOINTED alias must be refused, not followed ==
[claude] Created: installed flaui-mcp@flaui-mcp-marketplace
[claude] WARNING: disabled 1 conflicting marketplace copy/copies of the driving skill (they will be re-enabled if you uninstall flaui-mcp, and reinstalled first if anything has removed them).
Some targets did not complete - see C:\Users\user\AppData\Local\Temp\flaui-smoke2-535c04a2-ace6-4aa2-afd6-278b9dbd547f\data\install.log
If you configured agy, restart it to load the new tools.
If you configured Claude Code, quit the client completely and relaunch - it registers plugin hooks only at startup, so a new session is not enough.
Tip: to WATCH the agent act on screen, run  flaui-mcp overlay on  (off by default; flaui-mcp overlay off to disable), then reconnect. See the README's "Watching & auditing the agent" section.
  PASS  the alias could be repointed (the check is able to run at all)
[claude] Removed: claude plugin + marketplace removed
[claude] WARNING: flaui-mcp@flaui-mcp (scope user) is no longer installed, and the marketplace `flaui-mcp` now points at github `someone-else/a-different-fork` instead of the github `ckir/flauimcp` we recorded - so it was NOT reinstalled and your marketplace was NOT changed. To restore it yourself once `flaui-mcp` points where you expect: claude plugin install flaui-mcp@flaui-mcp --scope user.
Some targets did not complete - see C:\Users\user\AppData\Local\Temp\flaui-smoke2-535c04a2-ace6-4aa2-afd6-278b9dbd547f\data\install.log

  PASS  restore REFUSED to install from a repointed alias
  PASS  and installed nothing

ALL CHECKS PASSED

```
