# Structured server log — design

Status: design approved section by section (2026-11-07). TODO.md "Unscheduled → Structured logging".
AGY-FIRST consult ALIGNED (seam `.clavity/seams/logging-first.md`).

## 1. Goal and success criterion

The flaui-mcp server writes a durable, structured log that an **agent** reads after a live session went
wrong, to pinpoint the defect.

**Success criterion.** Given a session in which a tool call misbehaved, an agent handed only the log file
can name the failing call (tool, target, structural arguments), its outcome code, its duration, and the
server-internal events correlated to that call — without reading stderr, and without the log ever
containing user content (typed, pasted, set or clipboard text; captured screen text; images).

## 2. Decisions (operator, 2026-11-07)

| # | Decision |
|---|---|
| D1 | The reader is an agent: JSON Lines, fixed field names, a correlation id per call, outcome codes that are the `ToolErrorCode` names of `docs/agent-contract.md`. |
| D2 | Arguments: **structural** arguments are logged raw; **content** arguments are replaced by `{len, sha8}`. Results are never logged beyond outcome code, message and counts. |
| D3 | **On by default**, bounded, under `%LOCALAPPDATA%\flaui-mcp\logs\`; `--no-log` opts out. |
| D4 | **UI text** (window titles, element names, selector values, and error messages that quote them) is logged raw, **except** when the call's target window is sensitive (§6.3), in which case it is hashed. |
| D5 | Approach A: `Microsoft.Extensions.Logging` + an in-repo JSONL file provider + one MCP `CallToolFilter`. No new third-party dependency. |

Rejected: Serilog (third-party deps for what ~200 lines provide), a static writer outside MEL (a second
logging system), an MCP tool to read the log (YAGNI: the error response names the file, §5.5).

## 3. Current state (verified 2026-11-07)

- `src/FlaUI.Mcp.Server/Program.cs:98-99`: `ClearProviders()` + console provider, all levels to stderr.
  stdout is the MCP JSON-RPC channel.
- Ad-hoc stderr writers: `InputAudit` (constructed at `Program.cs:150` over `Console.Error`),
  `ClipboardAccess.cs:47`, `InputTools.cs:327` (`AuditForceOverwrite`), `TtsSignal.cs:88`,
  `ElevationGuard` (`Program.cs:32`).
- Only durable file: `ServerStateFile.TryLogStartupError` → `%LOCALAPPDATA%\flaui-mcp\startup-errors.log`
  (`src/FlaUI.Mcp.Server/Install/ServerStateFile.cs:44-53`).
- `src/FlaUI.Mcp.Core` has **no** `ILogger` usage at all.
- `ToolResponse.Guard`/`GuardImage` (`src/FlaUI.Mcp.Server/Tools/ToolResponse.cs:22-58`) map
  `ToolException` to `{error, message, suggestedRecovery}` and any other non-critical exception to
  `INTERNAL` carrying **only `ex.Message`** — type and stack are lost. Most tools return the error as a
  normal text result (not `IsError`).
- `StaThreadContext.RunAsync` (`src/FlaUI.Mcp.Core/Threading/StaThreadContext.cs:31-39`) queues bare
  `Action`s on a `BlockingCollection` drained by a dedicated thread: the caller's `ExecutionContext`
  (hence any `AsyncLocal` / logger scope) does **not** flow to the work item.
- ModelContextProtocol 1.4.0: `IMcpServerBuilder.WithRequestFilters(Action<IMcpRequestFilterBuilder>)`
  and `IMcpRequestFilterBuilder.AddCallToolFilter(McpRequestFilter<CallToolRequestParams, CallToolResult>)`;
  `CallToolRequestParams.Arguments` values are `JsonElement`.
- 53 `[McpServerTool]` methods in 13 files under `src/FlaUI.Mcp.Server/Tools/`.
- Error messages quote UI text, e.g. `WindowManager.cs:585` `"No window titled '{title}'."`,
  `WaitCoordinator.cs:237` `"... {by}={value} ..."`.

## 4. Components

All new server-side types live in `src/FlaUI.Mcp.Server/Logging/`.

### 4.1 `JsonlFileLoggerProvider` (`ILoggerProvider`)
- Producer side: `ILogger.Log` builds a `LogRecord` **on the calling thread**, copying `cid`, `tool` and
  `sensitive` out of the external scope at that moment, and enqueues it onto a bounded
  `System.Threading.Channels.Channel` (capacity **4096**, `TryWrite`; never blocks). A failed `TryWrite`
  increments a dropped counter. Scope values are never read on the writer thread: it does not share the
  caller's `AsyncLocal` state, and by the time it dequeues, the call's scope may already be disposed.
- Consumer: one dedicated background thread serialises each record to a UTF-8 line (§5) and writes it with
  a **single** `Write` of the complete buffer (record + `\n`). It flushes when the channel is empty after
  a write (once per burst, not once per record).
- File: `%LOCALAPPDATA%\flaui-mcp\logs\flaui-mcp-<pid>-<start>-<seg>.jsonl`, where `<start>` is the
  process start time in UTC as `yyyyMMddTHHmmssZ` and `<seg>` a two-digit segment number from `01`. The
  start time is in the name because Windows reuses pids: a pid-only name lets a later process append to a
  dead one's file and interleave two sessions. Opened `FileMode.Append`, `FileAccess.Write`,
  `FileShare.ReadWrite | FileShare.Delete`.
- Rolling: at **5 MB** the writer closes segment N and opens segment N+1. **No file is ever renamed**:
  renaming fails with a sharing violation while any reader holds the file without `FileShare.Delete`, and
  agents tail these files. After opening a new segment, the writer deletes this process's segments older
  than the newest 5; a delete that fails (file held open) is skipped and retried at the next roll.
- Serialisation is System.Text.Json with its default encoder only (never string concatenation): control
  characters in agent-supplied strings are escaped, so a `\n` in an argument cannot forge a record.
- Implements `ISupportExternalScope`, which is how it sees the `cid`, `tool` and `sensitive` values the
  filter puts in scope (§4.2, §6.3).
- Exposes `CurrentFilePath` (the active segment's absolute path), read by §4.5.
- Startup pruning: a file belongs to a dead process when no live process has that pid AND that start
  time (same liveness rule as `ServerStateFile.PruneDead`). Keep the newest **20** dead-process files
  overall, delete the rest. Never touch a live process's files.
- After any successful write following drops, emit one `log.dropped` record with the count since the last
  report.
- Minimum level from `--log-level` (default `info`).

### 4.2 `ToolCallLogFilter` (MCP `CallToolFilter`)
Registered in `Program.cs` via `.WithRequestFilters(f => f.AddCallToolFilter(...))` on the existing
`AddMcpServer(...)` chain. Per call:
1. Generate `cid` = `c-` + 8 lowercase hex chars (random).
2. Decide `sensitive` for the call (§6.3) and `using` an `ILogger.BeginScope` carrying `cid`, `tool` and
   `sensitive`.
3. Emit `tool.start` with classified `args` (§6).
4. `await next(...)`, timing with `Stopwatch`.
5. Read the outcome from the result: `IsError` content, or a text result whose JSON has a top-level
   `error` string → that code; otherwise `ok`. Emit `tool.end` with `outcome`, `ms`, and `msg` (the error
   message, UI-text-masked per §6.3) when not ok.
- Any exception thrown by the filter's OWN logic is caught and swallowed; `next` still runs and its result
  is returned unchanged. Exceptions from `next` itself propagate unchanged (after a `tool.end` with
  `outcome:"INTERNAL"`), except `OutOfMemoryException` which propagates without logging work.

### 4.3 `ArgumentClassifier`
Turns the `JsonElement` argument dictionary into the logged `args` object (§6).

### 4.4 Correlation across the STA thread (Core)
`StaThreadContext.RunAsync` captures `ExecutionContext.Capture()` at enqueue time and runs the work item
under `ExecutionContext.Run(captured, ...)` (null capture → run directly). Effect: logger scopes and any
`AsyncLocal` set by the caller are visible inside UIA work. No other behaviour changes.

Only the **query** STA needs this. ACTION work already runs on a new `Thread` per action
(`AutomationDispatcher.RunActionAsync`, thread `uia-action-sta`), and `Thread.Start` flows the
starting thread's `ExecutionContext`. When an action times out (`AwaitWithTimeout`,
`AutomationDispatcher.cs:105-119`) its thread is abandoned but keeps running; any line it logs later still
carries the original call's `cid`. That is the correct attribution (the line describes that call's
action), and a reader sees it as a line timestamped after that call's `tool.end`.

### 4.5 Exception capture at the tool boundary
`ToolResponse.Guard`/`GuardImage` gain an `ILogger` source (a static accessor set once at startup, null
when logging is off — the two methods are static today and are called from 53 tool methods; threading an
instance through every tool is out of proportion). The accessor has an `AsyncLocal` override that tests
use, so parallel tests never see each other's logger through the static. On the generic-exception branch they log
`tool.exception` with `exType`, `hresult` and `stack`. `ToolException` logs nothing extra (its code and
message are in `tool.end`). The `OutOfMemoryException` / `OperationCanceledException` filter is unchanged.

The `log` error field (§5.5) needs the current `cid` and file path, which `ILogger` does not expose. The
same static accessor therefore holds a `Func<string?> LogPointer` that returns
`"<cid> in <CurrentFilePath>"`, or null when the provider is off or no `cid` is in scope; the filter
publishes the call's `cid` in an `AsyncLocal` alongside the logger scope so `LogPointer` can read it. The
error DTO omits `log` when the pointer is null (never `"log": null`; `ToolResponse.Json` at
`ToolResponse.cs:18` does not ignore nulls, so the omission is explicit in the DTO).

### 4.6 Core instrumentation
`src/FlaUI.Mcp.Core` takes a dependency on `Microsoft.Extensions.Logging.Abstractions`. Instrumented
classes receive an `ILogger<T>` through their constructor with a `NullLogger<T>.Instance` default so that
existing test constructions compile unchanged.

## 5. Record format (contract)

One JSON object per line, UTF-8, `\n`-terminated. Field names below are fixed; adding a field is
compatible, renaming or removing one is a breaking change.

| Field | Type | Presence | Meaning |
|---|---|---|---|
| `ts` | string | always | UTC, ISO-8601 with milliseconds and `Z` |
| `lvl` | string | always | `debug` \| `info` \| `warning` \| `error` |
| `pid` | number | always | server process id |
| `ev` | string | always | event name, §5.2 |
| `cid` | string | in a call | correlation id; absent outside a tool call |
| `tool` | string | in a call | MCP tool name |
| `args` | object | `tool.start` | classified arguments, §6 |
| `outcome` | string | `tool.end` | `ok`, a `ToolErrorCode` name, or `INTERNAL` |
| `ms` | number | `tool.end`, `wait.end` | elapsed milliseconds |
| `msg` | text | optional | human/agent-readable text, UI-text-masked per §6.3 |
| `trunc` | bool | when true | the record was cut to the 8 KB cap |
| event fields | various | per event | §5.2 |

**Masked values.** Any value the classification rules (§6) hide is written as the object
`{"len": <chars>, "sha8": "<8 hex>"}`. Type `text` above means "a JSON string, or that masked object" -
the same rule for `msg`, every starred event field (§5.2), and every string leaf in `args`. A reader tests
for an object to know a value was masked.

### 5.1 Size cap
A serialised record is at most **8192 bytes**. If larger, shorten in this order until it fits, setting
`"trunc":true`: (1) `stack`, cut to its first frames; (2) the longest raw string leaves in `args` and event
fields, each cut with a trailing `...`; (3) `msg`, last, because it carries the failure explanation;
(4) if it still does not fit, `args` is replaced by `{"omitted": <original byte length>}`. Masked objects
inside a kept `args` and the fixed fields (`ts`, `lvl`, `pid`, `ev`, `cid`, `tool`, `outcome`, `ms`) are
never cut. A record never spans lines.

### 5.2 Events (v1, closed set)

| `ev` | lvl | Emitted by | Event fields |
|---|---|---|---|
| `server.start` | info | Program.cs | `version`, `flags` (array of the server flags present), `rulesSha` (or null), `dpiAwareness`, `dpiScale`, `session` (`console`\|`rdp`\|`unknown`), `readOnly` |
| `server.stop` | info | ProcessExit / host stop | `uptimeMs`, `calls`, `droppedTotal` |
| `window.bind` | info | WindowManager, when a `wN` handle is first minted | `handle`, `hwnd`, `pid`, `process`, `title`* |
| `tool.start` | info | filter | `args` |
| `tool.end` | info (ok) / warning (error) | filter | `outcome`, `ms`, `msg?` |
| `tool.exception` | error | ToolResponse.Guard / GuardImage | `exType`, `hresult`, `stack`, `msg` |
| `selector.resolve` | debug | PerceptionManager find path | `by`, `value`*, `candidates`, `culled`, `chosen` |
| `ref.resolve` | info | where a tool turns an `eN` argument into an element | `ref`, `controlType`, `automationId`*, `name`*, `found` |
| `action.blocked` | warning | when `ActionBlockedPending` is raised | `fgHwnd` (number, raw HWND; `0` when there is no foreground window), `fgHandle` (the `wN` string if that HWND is already bound, else null), `fgProcess` (null when unresolvable), `fgTitle`* (null when unresolvable) - the foreground window at timeout, usually the modal |
| `focus.attempt` | info | WindowManager focus path | `target`, `gained`, `fgProcess?`, `fgHandle?` (when not gained) |
| `input.decision` | info | InputGuard | `lease` (`valid`\|`absent`\|`expired`\|`invalid`), `budgetLeft`, `allowed`, `code?` |
| `wait.end` | debug | WaitCoordinator | `condition`, `polls`, `ms`, `satisfied` |
| `capture.decision` | info | WindowCaptureCoordinator | `method`, `breaker` (`closed`\|`open`), `refused?` |
| `log.dropped` | warning | provider | `count` |

\* = a **UI-text field**. The provider hashes every starred field, and `msg`, when the scope's `sensitive`
is true (§6.3). `window.bind` happens outside any call scope as often as inside one, so its `title` is
classified by the bound window's own process with the §6.3 predicate. A new event that carries UI text
must star the field; the format-pin test (§8.1) holds the starred list.

`window.bind` exists because `w3` means nothing once the session is gone: it is what lets a post-mortem
reader turn a handle in `args` back into a process and window. `ref.resolve` does the same for an `eN`
argument, once per call that uses one (never per snapshot, which mints hundreds of refs).
`action.blocked` names the window that took the foreground when an action hung, which is usually the modal
that blocked it; Win32 only (`GetForegroundWindow`), no UIA.

The existing stderr audit sites (`InputAudit`, `ClipboardAccess.cs:47`, `InputTools.cs:327`) are left
unchanged and are NOT mirrored into the log: the filter already records every one of those tool calls
with its outcome, and a second event per call adds a schema without adding a fact. Exact instrumentation
points inside PerceptionManager / WindowManager / the ref-resolving paths are fixed in the plan against
the code.

### 5.3 Reader rule
A reader skips a **final** line of the **newest** segment that does not parse (the process may have died
mid-write). Every other line parses. One process's segments are read in ascending `<seg>` order.

### 5.4 Flags
`--no-log` disables the file provider entirely. `--log-level=debug|info|warning` (garbage → `info`).
Parsed in `ServerOptions.FromArgs` like the existing flags. Stderr console logging is unchanged.

### 5.5 Error-response pointer
When the file provider is active, every error JSON produced by `ToolResponse` gains an additive field
`"log": "<cid> in <absolute log file path>"`. With `--no-log` (or no `cid` in scope) the field is
omitted. Additive: existing agents ignore it. Documented in `docs/agent-contract.md`.

## 6. Argument and text classification

### 6.1 Parameter classes
- **Structural** — logged raw: parameters marked `[LogStructural]` on the tool method parameter.
  Intended for handles (`window`), refs, `by`, enum-like actions, chords, timeouts, flags, offsets.
- **UI text** — logged raw unless sensitive (§6.3): parameters marked `[LogUiText]` — window titles,
  element names, selector `value`, find-text queries.
- **Content** — everything else that is a string: replaced by `{"len": <chars>, "sha8": "<first 8 hex
  of HMAC-SHA256(key, UTF-8 bytes)>"}`. `key` is 32 random bytes generated at process start and never
  written anywhere. A plain hash would let anyone holding the log brute-force a short secret (a 4-6 digit
  PIN is at most a million guesses); keyed, the hash only tells a reader that two values in the SAME
  session's files were equal, which is the diagnostic use. Unmarked = content: classification fails
  closed.
- Numbers and booleans are logged raw regardless of marking.
- **Complex parameter types** (records bound from a JSON object, e.g. `Selector`,
  `src/FlaUI.Mcp.Core/Perception/Selector.cs:16-22`, taken by 20 tool parameters) are classified PER
  PROPERTY with the same attributes, placed on the record's properties. The attributes therefore live in
  Core (`FlaUI.Mcp.Core.Logging`), not Server, so Core records can carry them. For `Selector`:
  `AutomationId` and `Name` are `[LogUiText]`; `NameMatch`, `ControlType`, `Scope` are
  `[LogStructural]`; `IgnoreCase` is a bool. An unmarked string property is content, and any string leaf
  of an array, a dictionary, or a JSON value not bound to a declared property is content.

### 6.2 Coverage guard
A reflection test enumerates every `[McpServerTool]` method's parameters and, recursively, the `string`
properties of every complex parameter type (e.g. `Selector`), and asserts each `string` is
`[LogStructural]`, `[LogUiText]`, or listed in the test's explicit "content on purpose" set. A string that
lives inside a complex type is checked exactly like a top-level one; checking only top-level `string`
parameters would skip all 20 selector parameters. A new tool
with an unclassified string fails the build's test gate. The same test holds the reverse direction: the
known content parameters (the typed/pasted/set text of `desktop_type`, `desktop_paste_text`,
`desktop_set_value`, and the text of `desktop_clipboard_set`; exact parameter names fixed in the plan) must
carry NEITHER attribute, so marking one raw to silence the guard fails the test.

### 6.3 Sensitive target window
A call's target window is the one named by its `window` argument, resolved to a process name with
`WindowManager.TryGetAppName` (`WindowManager.cs:157-162`: pure Win32, no UIA, safe off the STA thread, so
the filter never queues behind automation work).
It is **sensitive** iff `PerceptionPolicy.IsDenied(processName) ||
SensitivityClassifier.CouldMatchProcess(processName)` — the same process predicate
`PerceptionManager.DenylistedWindowsVisibleAsync` uses (`PerceptionManager.cs:1361-1365`) plus the
operator's rules. When the target is sensitive, **or** a `window` argument is present but cannot be
resolved, every UI-text value of that call — in `args`, `msg`, and internal events in the call's scope — is
hashed like content. Window-level, deliberately coarse (fails toward masking).

Calls with **no** `window` argument split in two:
- **Foreground-acting** calls - an input tool that acts on the current foreground window when `window` is
  omitted (e.g. `desktop_key`, `InputTools.cs:335-339`; the full list is fixed in the plan from the tool
  descriptions). The target IS the foreground window: the filter resolves it with `GetForegroundWindow` +
  `GetWindowThreadProcessId` (Win32, off the STA thread) and applies the predicate above. No foreground
  window, or one that cannot be resolved, counts as sensitive.
- **Targetless** calls (e.g. `desktop_open_window` by title, `desktop_find_text` across the desktop)
  have no target to classify. Their UI text is logged raw when no `--redaction-rules` file is loaded
and hashed when one is: an operator who wrote rules has said some UI text is sensitive, and without a
target we cannot tell which. Rationale for raw by default: the main no-window failure is
`WindowNotFound`/`AmbiguousMatch` on a title, and hashing it removes the one fact that explains it.
Exception: an `AmbiguousMatch` message lists the candidate windows' titles (`WindowManager.cs:589-590`)
and could name a denylisted window. On a targetless call, a `tool.end` with outcome
`AmbiguousMatch` therefore omits `msg` entirely. The agent already received the candidate list in the
tool response; the log keeps the outcome code, which is what identifies the failure.

## 7. Failure behaviour

| Failure | Behaviour |
|---|---|
| Log dir/file cannot be created at startup | one stderr line + `ServerStateFile.TryLogStartupError`; the server **starts** with the file provider off |
| Write error while running (disk full, AV lock) | caught on the writer thread, counted; reopen attempted at most every 30 s; tool calls unaffected |
| Roll fails (next segment cannot be created) | keep appending to the current segment past 5 MB and retry the roll at the next write; never stop writing over a roll |
| Old segment cannot be deleted (held open by a reader) | skipped, retried at the next roll; never blocks writing |
| Channel full | record dropped and counted; `log.dropped` later; never blocks a call |
| Exception in filter/classifier code | swallowed; the tool's result is returned unchanged |
| Process killed mid-write | at most the final line is torn (single-buffer writes); reader rule §5.3 |
| Clean shutdown | `server.stop` is ENQUEUED first, then the channel is completed and the writer drains for up to 1 s (so `server.stop` is the last record written, not a record left behind) |
| stdout | the provider never writes to stdout |
| Disk use | 5 MB × 5 segments per live process; dead-process files capped at the newest 20 |

## 8. Testing

Headless (part of the default test run):
1. **Format pin** — golden records per `ev`; fixed field names; the starred UI-text field list; masked
   values are the `{len, sha8}` object; 8 KB cap with `trunc`, truncation order §5.1 (`msg` survives a
   record whose `stack` alone exceeds the cap).
2. **Provider** — rolling to a new segment at the cap with no rename, keeping 5 segments; a segment held
   open by a reader does not stop rolling or writing; pruning keeps live processes; write failure does not
   throw; full channel counts drops and returns immediately; scope values captured on the producer thread
   (a record logged inside a scope that is disposed before the writer dequeues still carries its `cid`);
   `server.stop` is the last record after shutdown; torn final line handled by a reader helper that
   implements §5.3.
3. **ArgumentClassifier** — structural raw; unmarked string → `{len, sha8}`; UI text raw vs hashed by
   sensitivity; unresolvable window → hashed; foreground-acting call with a sensitive foreground process
   → hashed; a `Selector` argument logs `Name`/`AutomationId` raw on a non-sensitive window, hashed on a
   sensitive one, and an unknown extra JSON property inside it as content; **near-miss distractor**: a structural `textLength`-style
   name is not treated as content by name, and a content parameter named like a structural one is still
   hashed (classification is by attribute, never by name).
4. **Coverage guard** — §6.2.
5. **No content on disk** — drive `desktop_type`, `desktop_paste_text`, `desktop_set_value`,
   `desktop_clipboard_set` through the filter with a sentinel string; assert the sentinel's UTF-8 bytes
   occur **nowhere** in the log file. Mutation check: removing the hashing turns it red.
6. **STA correlation** — a log line written inside `StaThreadContext.RunAsync` carries the caller's `cid`;
   mutant (no `ExecutionContext` capture) turns it red.
7. **Guard** — `INTERNAL` writes `tool.exception` with `exType` + `stack` and the error JSON carries `log`;
   `ToolException` writes no stack; `OutOfMemoryException` still propagates; with no pointer the error
   JSON has NO `log` key (not `"log": null`).
8. **Server process** — real exe over stdio (harness of
   `test/FlaUI.Mcp.Tests/Server/StdioProtocolCleanlinessTests.cs`), one failing call: stdout lines are all
   JSON-RPC; the log has `server.start`, `tool.start`, `tool.end` with one shared `cid`; the error's `log`
   names that `cid` and file. With `--no-log`: no file, no `log` field.

No new Desktop-trait tests: internal events are covered with test doubles; the process test needs no
desktop interaction beyond a deterministic failing call (e.g. an unknown window handle).

## 9. Documentation
- `docs/agent-contract.md`: new "Server log" section — location, record format (§5), event list, reader
  rule, the `log` error field.
- `docs/operator-manual.md`: `--no-log`, `--log-level`, location, retention, privacy rules (§6).
- `CHANGELOG.md` `[Unreleased]`; tick the TODO.md item.

## 10. Out of scope (v1)
Logging every bare `catch` (that is R13), per-element tree dumps, per-stage OCR timing, an MCP log-reading
tool, log shipping, a per-SNAPSHOT ref-binding event (a snapshot mints hundreds of refs). Per-CALL
resolution of an `eN` argument IS in v1, as `ref.resolve` (§5.2). Each is a new `ev` later without a format break.

## Stand-downs
- REJECTED (panel r2, agy Q2: replace the keyed hash with plain SHA-256 because a local reader already has
  process access): the threat is not a local reader. The log's purpose is to be handed to an agent,
  attached to an issue, or pasted into a transcript; off the machine, an unkeyed hash of a 4-6 digit PIN
  is reversed by enumerating at most 10^6 inputs. Tests needing determinism inject the key.

## 11. Open items resolved in the plan (against the code)
- Exact instrumentation call sites for `selector.resolve`, `focus.attempt`, `wait.end`,
  `capture.decision`, `input.decision`.
- How `server.start` obtains `dpiAwareness`, `dpiScale` and `session` (Win32 calls; measured).
- The parameter-by-parameter `[LogStructural]` / `[LogUiText]` assignment for all 53 tool methods.