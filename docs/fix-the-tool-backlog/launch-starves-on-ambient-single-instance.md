# launch-starves-on-ambient-single-instance — `desktop_launch_app` times out with a message that points away from the cause

- **Captured:** 2026-07-29 (via flaui-autotrain)
- **Regression test:** none — see Test-gen note
- **Trait:** n/a (no test generated)

## Steps to Reproduce

1. Have an instance of a **single-instance** app already running — VS Code is the case this was observed
   on; the new Win11 Notepad shows the same shape (a second launch tabs into the existing process).
2. Call `desktop_launch_app` with that app's exe path and no instance-isolating arguments.
3. It blocks for the full `timeoutMs` and then throws `LaunchTimeout`:
   *"`<path>` started but showed no titled window within `<N>` ms"*, with the remedy *"increase timeoutMs
   or check for a splash screen"*.

Both halves of that message are wrong for this case. The app started fine and its window appeared almost
immediately; and increasing `timeoutMs` can never help, because nothing about waiting longer changes
which pids are eligible.

## Code-level Mitigation

`WindowManager.LaunchAppAsync` (`src/FlaUI.Mcp.Core/Windows/WindowManager.cs:396-439`) snapshots every
existing pid at `:400`, then accepts a window only from the launched pid (`:425`) or from a **new** pid
sharing the launched exe's base name (`:431`). When the app is single-instance, the process we start
hands off to the ambient instance and exits; the window that appears belongs to a **pre-existing** pid,
so both branches reject it and the loop starves to the throw at `:436`.

**The filtering itself is correct and must not change** — returning a window we did not start would be a
far worse bug, and that hypothesis was explicitly measured and refuted during the A1a work.

The defect is the **diagnostic**. Before throwing at `:436`, check whether any process in the
`preExistingPids` snapshot shares `expectedProcessName`. If one does, throw a message that names the
actual cause and the actual remedy instead of the generic timeout — that an instance was already
running, that only a newly-started pid is eligible by design, and that the caller should pass
instance-isolating arguments (for Chromium/Electron, a fresh `--user-data-dir` plus `--new-window`).

Worth stating in the same message, because it is the next trap: a pristine profile yields the
**first-run** UI — trust prompts, welcome tabs, extension toasts — which changes the node count. So
isolation alone trades one nondeterminism for another unless first-run is suppressed too.

## Test-gen note (why no runnable test was generated)

The repro is expressible as a `desktop_*` call, but every available trigger depends on a **third
party's** single-instance behaviour rather than on ours: VS Code's IPC hand-off, or the new Notepad's
tabbing. A test would pin someone else's implementation detail — and would go green not when we fixed
the message, but whenever Microsoft changed how Notepad launches.

The defect is in the wording of one throw site, and the condition that should gate it
(`preExistingPids` containing a process with `expectedProcessName`) is fully determined by our own code.
When this is fixed, the honest test is a **unit** test of that decision, not an end-to-end launch of a
third-party app — which is why one is not stubbed here.
