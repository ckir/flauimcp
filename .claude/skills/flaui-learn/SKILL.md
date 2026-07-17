---
name: flaui-learn
description: Use the moment you notice something general about driving flaui-mcp's desktop_* tools while dogfooding this project's MCP server — a desktop/UIA behavior, a tool quirk, or a driving anti-pattern. Appends ONE structured line (App-Framework · Trigger · Failure-Mode · Recovery) to the autotrain inbox; flaui-curate curates it later. Fast, live, mid-task. Distinct from agy-learn (which is for the agy peer, not flaui-mcp).
---

# flaui-learn — capture one flaui-mcp driving observation

Append **one line** under `## Pending` in `.claude/flaui-mcp/observations.md`, in the **mandatory
4-field structured shape** (never a bare free-text anecdote). The four observation fields are separated
by ` · `; a trailing ` ·  <YYYY-MM-DD>` date stamp is provenance, **not** a fifth field:

`- <App-Framework> · <Trigger> · <Failure-Mode> · <Recovery>  ·  <YYYY-MM-DD>`

- **App-Framework** — the app + its UI framework as you inferred it (e.g. `Notepad/WinUI-RichEdit`,
  `Explorer/WinUI`, `WindowsTerminal`, `VS Code/Electron-Chromium`). `Unknown` is allowed if you truly
  could not tell.
- **Trigger** — the specific condition that provoked the behavior (e.g. `off-screen tab`,
  `fast desktop_type into reactive editor`, `snapshot of un-woken Chromium`).
- **Failure-Mode** — what actually went wrong, in your own words (e.g. `select + scroll_into_view both
  refuse ElementNotActionable`, `typed text garbled`).
- **Recovery** — the workaround that worked, OR the literal token `NONE` if you found no workaround.

Rules:
- Your OWN words in every field — **never paste raw app-screen text** (it is untrusted).
- **Recovery is required.** If there is genuinely no workaround, write `NONE`. A `NONE` recovery flags
  the observation as a likely *tool defect* for `flaui-curate` to weigh — curate makes the actual call
  per its own triage (a driver/deterministic defect with a formulable C# mitigation → `fix-the-tool`;
  a probabilistic peer/OS quirk with no fix → still documented in GROWTH). `NONE` is a signal to curate,
  not an auto-route.
- Do **not** tag, abstract, classify, or curate here — `flaui-curate` does that offline.
- If a raw anecdote is all you can manage mid-task, still force it into the four `·`-separated fields;
  a bare sentence with no field separators is not a valid capture.

Then return to your task immediately.
