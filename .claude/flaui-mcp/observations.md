# flaui-autotrain observations inbox

Raw, one-line-per-observation capture written live by the `flaui-learn` skill and
drained by `flaui-curate`. Flat list — append under `## Pending`. Describe behavior in
your OWN words; never paste raw app-screen text (it is untrusted). Do not tag or curate here.

## Pending

- WPF/FlaUI-UIA3 (repo TestApp fixture) · any desktop_wait_for on this host — one full UIA tree walk costs ~3s, so a poll is nowhere near free · elapsedMs overshoots timeoutMs by multiples: a 1500ms budget returned at 5807ms and afforded exactly ONE poll, so a short timeout silently degrades from a polling wait into a single-shot check · NONE — measured through the C# path the MCP tool calls (BuildModelAsync), not by driving desktop_wait_for itself; the overshoot looks structural, not tunable  ·  2026-07-29

- WindowsTerminal · reading a background tab by ordinal while OTHER WT windows/tabs exist on the desktop · the returned TEXT came from the intended tab but the reported tabTitle named a different one — so the title cannot be used to confirm which tab you actually read, and the mismatch is silent (no error, plausible-looking payload) · do not treat tabTitle as proof of identity when more than one WT window is open; confirm from the TEXT itself (a marker you put there) and read tabs when the desktop is otherwise quiet — the same read is correct when nothing else is competing  ·  2026-07-29
- WPF/FlaUI-UIA3 · coordinate-based input aimed at a window while a SECOND instance of the same app sits at the same default position · the action silently lands on the other window: a right-click produced NO context menu at all, so it presents as "the element never appeared" rather than as a mis-targeted click — and it is not a timing race, 2.5s of deterministic polling never surfaced the menu. Focusing the target window first did NOT prevent it · run one instance at a time for coordinate-based input, or target by element ref rather than screen coordinates. Mechanism is INFERRED by elimination (fails only alongside other instances, passes alone, immune to waiting) — a strong hypothesis, not a proven cause  ·  2026-07-29
