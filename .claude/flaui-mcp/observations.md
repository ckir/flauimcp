# flaui-autotrain observations inbox

Raw, one-line-per-observation capture written live by the `flaui-learn` skill and
drained by `flaui-curate`. Flat list — append under `## Pending`. Describe behavior in
your OWN words; never paste raw app-screen text (it is untrusted). Do not tag or curate here.

## Pending



- Notepad (Win11 store build)/WinUI-RichEdit · desktop_type of a 56-char mixed alpha+digit string at the DEFAULT pacing (interKeyDelayMs=15) into a blank tab's document body · no failure observed - the text landed byte-exact, desktop_type's own verify reported ran/verified true with mismatch false, and an independent desktop_get_text matched all 56 chars · NONE NEEDED (nothing to recover from) - the reactive-editor garble the manual warns about did NOT reproduce at default pacing on this build, so treat paced typing as sufficient here and reach for set_value/paste_text only on a real mismatch; a 0-delay contrast run was NOT attempted, so this says nothing about the raw atomic blast  ·  2026-09-09



- Explorer/WinUI context-menu popup · sending desktop_key with a window handle but NO ref while a PopupHost menu is open · the chord does not reach the menu - it lands on the owning Explorer window's own Pane, the highlight never moves, and the call still returns ok:true, so nothing signals the misroute (desktop_get_focused_element afterwards named a Pane in the Explorer window, not a menu item) · do not steer one of these menus by bare chords; resolve the MenuItem with desktop_find and invoke/click that ref instead. NOTE: this leg was measured by a dispatched subagent and I did NOT re-measure it myself, unlike the snapshot/find result above which I reproduced first-hand  ·  2026-09-09







- Explorer/Shell folder view (Win11), flaui-mcp driven from a background server process · focusing a VISIBLE, non-minimized, non-foreground window to reproduce the documented foreground-lock · the lock did NOT occur - desktop_focus_window returned foregroundGained:true and the window took foreground, twice, though the manual states unconditionally that a background-process server cannot SetForegroundWindow an existing window past the active one · UNCONFIRMED MECHANISM, not isolated: the whole run happened at the PHYSICAL console with the operator present, an active input lease, and synthetic input having fired moments earlier, so recent REAL human input is the obvious suspect - it lines up with the earlier measured note that Windows refuses a foreground change to a process with NO recent real human input. Treat the foreground-lock as CONDITIONAL on input recency rather than as a property of being a background process, and re-test it on an idle unattended box before trusting either wording  ·  2026-09-09


- VS Code/Electron-Chromium · wake_accessibility lifecycle on an already-hydrated window · no failure - wake returned {wakeId k1, alreadyAwake:false} on a tree that was already full, a second wake returned the SAME wakeId with alreadyAwake:true, list_wakes showed the single held wake, release_accessibility succeeded and list_wakes went empty · idempotency and release behave as documented; note alreadyAwake reports whether THIS SERVER holds a wake, NOT whether the tree is hydrated - it said false on a fully-hydrated 199-node window, so it is a lease-style bookkeeping flag and must not be read as a hydration probe  ·  2026-09-09

- Settings/WinUI (ApplicationFrameHost) Windows Update page · running the dynamic-loading-ghost-ui task against a page the operator opened minutes earlier · trap UNEXERCISED, not passed - the page had fully settled ('up to date', timestamp populated, every control enabled, no placeholders), so there was no ghost UI to observe. Worth noting HOW that was established: desktop_snapshot_diff across the whole ~19s probe returned added/removed/changed all EMPTY, which is real evidence of a static page rather than a single glance that happened to look settled · to actually exercise this trap the page must be caught DURING a fetch, which needs input (clicking Check for updates) and so is an input-tier task, not a destructive:false one - the curriculum entry currently assumes a slow page will still be loading when you attach, and on a fast machine it will not be  ·  2026-09-09



- WindowsTerminal / any window, flaui-mcp driven from a background server · calling desktop_focus_window after the interactive session changed underneath the agent (physical console -> RDP reconnect) · the documented FOREGROUND-LOCK finally appeared, having refused to reproduce earlier the same day: {ok:true, foregroundGained:false, currentForeground:{handle:'0'}} - handle 0 meaning NO window owns foreground at all - plus the recommendedAction 'call-wait-for-foreground' and recovery fields that the earlier probe reported as ABSENT on its successful calls. desktop_wait_for_foreground then returned {foregroundGained:false, reason:'timeout', currentForeground:{handle:'0'}} across a 20s budget · when focus will not land, check the SESSION before re-looping the wait: qwinsta showed the session had moved back to '>rdp-tcp#0 Active' with console demoted to Conn, and the display had changed resolution and an app had a new pid - all signs the desktop under you is not the one you measured on. ⚠ CONFOUNDED, mechanism still NOT isolated: this supports the earlier hypothesis that the lock is conditional on recent real human input rather than on being a background process, but a session change is a second variable moving at the same time, so it corroborates rather than proves  ·  2026-09-09











