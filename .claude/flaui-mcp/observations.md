# flaui-autotrain observations inbox

Raw, one-line-per-observation capture written live by the `flaui-learn` skill and
drained by `flaui-curate`. Flat list — append under `## Pending`. Describe behavior in
your OWN words; never paste raw app-screen text (it is untrusted). Do not tag or curate here.

## Pending

- WPF/FlaUI-UIA3 (repo TestApp fixture) · any desktop_wait_for on this host — one full UIA tree walk costs ~3s, so a poll is nowhere near free · elapsedMs overshoots timeoutMs by multiples: a 1500ms budget returned at 5807ms and afforded exactly ONE poll, so a short timeout silently degrades from a polling wait into a single-shot check · NONE — measured through the C# path the MCP tool calls (BuildModelAsync), not by driving desktop_wait_for itself; the overshoot looks structural, not tunable  ·  2026-07-29

- Antigravity/Electron-Chromium · checking whether a GUI peer app is reachable before driving it · desktop_list_windows returns NO entry for the app at all while its process is very much alive — the window had been closed to tray/hidden, so a pure-Win32 top-level enumeration correctly sees nothing and it looks identical to "the app is not running" · cross-check with the OS process list and read MainWindowTitle: an alive process with an EMPTY MainWindowTitle means "running but no top-level window", which distinguishes a hidden/tray app from a dead one and from a broken tool — do not conclude the tool failed  ·  2026-07-29
