# FlaUI.Mcp Phase 3b-2 — Structured Content & Clipboard (v0.5.0) Design

**Status:** Draft for review (brainstorming → spec). Supersedes nothing; extends the
shipped v0.4.0 surface.

**Goal:** Add the first state-mutating *content* surface — targeted grid-cell read +
select, targeted text read, and clipboard get/set — built on the merged v0.4.0
perception/interaction infrastructure, with no synthetic input (that is Phase 4).

**Architecture in one line:** Targeted reads run on the existing **query** STA and
**replicate the snapshot security floor**; the one cell write reuses the Phase-3a
**action** STA path; clipboard is window-less global OS state reached via Win32 on a
transient STA hop.

---

## 1. Scope

### In scope (v0.5.0) — 5 tools

| Tool | R/W | Args | Returns |
| --- | --- | --- | --- |
| `desktop_get_grid_cell` | READ | `{window, ref, row, col}` | `{value, controlType, automationId, isPassword}` |
| `desktop_grid_select` | WRITE | `{window, ref, row, col}` | `{ok:true, pathUsed:"pattern"}` |
| `desktop_get_text` | READ | `{window, ref, selectionOnly?, maxLength?}` | `{text, truncated, isPassword}` |
| `desktop_clipboard_get` | READ | `{}` | `{text}` |
| `desktop_clipboard_set` | WRITE | `{text}` | `{ok:true}` |

`ref` always names an element from a prior `desktop_snapshot` (per-window, per-snapshot
scoped, as today). `row`/`col` are 0-based.

### Explicitly out of scope

- **`desktop_set_caret`, `desktop_select_text_range` → deferred to Phase 4.** UIA
  `TextPattern` can set a caret/selection, but nothing can *consume* that selection
  without synthetic input (type-over, `Ctrl+C`/`Ctrl+V`). They are inert until Phase 4
  and ship there, paired with `desktop_type`/`desktop_key`.
- **`desktop_patch_text` (server-side find/replace) — not built.** It is client-side
  composable from `desktop_get_text` → agent computes the new full string →
  existing `desktop_set_value` (ValuePattern). The server does not own string editing.
- **Chainable cell refs — not built.** `get_grid_cell` mints no ref (see §4). An agent
  needing to *act* on arbitrary cell content takes a targeted snapshot with the
  already-shipped `desktop_snapshot { rootRef: <gridRef>, maxDepth: 2 }`.
- **Non-text clipboard formats** (files, images, RTF/HTML) — `CF_UNICODETEXT` only.
- **Clipboard secret redaction** — impossible at this layer (see §5); documented, not
  attempted.

---

## 2. Tool contracts

### 2.1 `desktop_get_grid_cell` (READ, `readOnlyHint:true`)
- **Resolve** `ref` on the timeout-guarded transient **read** path (`RunOnRefReadAsync`,
  see §3 — transient STA + timeout but **no** offscreen preflight, so an off-screen grid is
  still readable). The element must support `GridPattern` (`PatternUnsupported` otherwise).
- **Bounds:** read `GridPattern.RowCount`/`ColumnCount`; if `row<0 || col<0 ||
  row>=RowCount || col>=ColumnCount` → `GridCellOutOfRange`.
- **Cell:** `GridPattern.GetItem(row,col)`; if it returns null → `GridCellOutOfRange`
  (`suggestedRecovery`: "scroll the grid to realize this row, then retry").
- **Value:** `cell.Patterns.Value.IsSupported ? Value.Value : cell.Name`. If
  `cell.Properties.IsPassword.ValueOrDefault` → `value="[REDACTED]"`, `isPassword=true`.
- **Returns:** `{ value:string, controlType:string, automationId:string, isPassword:bool }`.
- **Security:** denylist check on the owning process before reading (see §5) →
  `TargetDenied`.

### 2.2 `desktop_grid_select` (WRITE, `readOnlyHint:false, destructiveHint:true`)
- Routed through `InteractionTools.Act → GuardWrite → RunOnRefActionAsync` (action STA),
  so `--read-only-mode` blocks it.
- **Action:** `Interactor.GridSelect(grid, row, col)`:
  1. `GridPattern.GetItem(row,col)`, bounds-checked as in 2.1 → `GridCellOutOfRange`.
  2. **Offscreen preflight** — the action STA's `IsOffscreen` preflight guards the *grid*
     ref, **not** the cell `GetItem` returns. So check `cell.Properties.IsOffscreen`
     explicitly; if offscreen → `ElementNotActionable` (`suggestedRecovery`: "scroll the
     grid to bring the cell on-screen, then retry"). This preserves the Phase-3a invariant
     that we never `Select()`/act on an off-screen element (COM-hang risk). Tradeoff: an
     agent cannot select a scrolled-off virtualized cell without scrolling first — correct
     and consistent with Phase 3a.
  3. `cell.Patterns.SelectionItem` — if unsupported throw `PatternUnsupported` naming
     **`"SelectionItem"`** (the missing pattern on the *cell*, not `"Grid"` on the parent),
     so the agent knows exactly what failed. Else `SelectionItem.Pattern.Select()`.
- **Returns:** `{ ok:true, pathUsed:"pattern" }` (matches the Phase-3a action envelope).

### 2.3 `desktop_get_text` (READ, `readOnlyHint:true`)
- **Resolve** `ref` on the timeout-guarded transient STA (see §3). The element must support
  `TextPattern` (`PatternUnsupported` otherwise).
- **Password short-circuit FIRST:** if the target element `IsPassword` →
  `{ text:"[REDACTED]", truncated:false, isPassword:true }` **before** touching
  `DocumentRange`/`GetSelection()` (never ask the provider for a secret's text/selection).
- **`selectionOnly`** (bool, default `false`): `false` → `DocumentRange.GetText(maxLength+1)`;
  `true` → first range of `GetSelection()` `.GetText(maxLength+1)`. `GetSelection()` is
  brittle (many providers throw `COMException`/`InvalidOperationException` when there is no
  selection rather than returning an empty array) → wrap it in try/catch and yield
  `{ text:"", truncated:false }` on failure, never let it bubble as `INTERNAL`.
- **`maxLength`** (int, default `10000`, clamp `1..200000`): request `maxLength+1` from UIA
  `GetText`; if the returned string length **strictly exceeds** `maxLength`, trim to
  `maxLength` and set `truncated:true`; otherwise `truncated:false` (so text whose length is
  *exactly* `maxLength` is **not** flagged truncated).
- **Returns:** `{ text:string, truncated:bool, isPassword:bool }` (the `isPassword` flag
  disambiguates a real `[REDACTED]` masked field from text that literally contains the
  string `"[REDACTED]"`).
- **Offscreen:** reading is allowed on an off-screen target (read path skips the action
  offscreen preflight — see §3), matching `desktop_snapshot includeOffscreen` semantics.
- **Security:** denylist check before reading (see §5) → `TargetDenied`.

### 2.4 `desktop_clipboard_get` (READ, `readOnlyHint:true`)
- Raw Win32 on a plain background `Task` (**no STA** — raw `user32` clipboard P/Invoke is
  apartment-agnostic; STA is only needed for the OLE `IDataObject` wrappers we are *not*
  using): `OpenClipboard(IntPtr.Zero)` with a bounded retry loop (5 attempts, ~50 ms
  backoff) → `GetClipboardData(CF_UNICODETEXT)` → `GlobalLock` → marshal the UTF-16 string
  → `GlobalUnlock` → `CloseClipboard` (always close in a `finally`).
- No text on the clipboard (`GetClipboardData` returns NULL) → `{ text:"" }` (success).
- `OpenClipboard` fails after retries (another process holds the lock) →
  `ClipboardUnavailable`.
- **Audit:** emit a one-line stderr audit-warn that the clipboard was read (no content
  logged). **No redaction** (see §5).
- **Returns:** `{ text:string }`.

### 2.5 `desktop_clipboard_set` (WRITE, `readOnlyHint:false, destructiveHint:true`)
- `GuardWrite` short-circuits to `WriteBlockedReadOnly` in `--read-only-mode`.
- Raw Win32 on a plain background `Task` (no STA, as 2.4). The Win32 memory protocol is
  exacting and MUST be implemented precisely:
  - **Empty-string fast-path:** `text==""` → `OpenClipboard` → `EmptyClipboard` →
    `CloseClipboard`; skip `GlobalAlloc`/`SetClipboardData` entirely.
  - Else: `GlobalAlloc(GMEM_MOVEABLE, (text.Length+1)*2)` — moveable memory is **required**
    (`SetClipboardData` rejects/misbehaves with `GMEM_FIXED`); size includes one trailing
    UTF-16 **null terminator** (a single wide `\0` = 2 zero bytes — *not* a double-null,
    which is only for multi-string formats).
  - `GlobalLock` → copy the UTF-16 chars → write the wide null at the end → `GlobalUnlock`.
  - `OpenClipboard` → `EmptyClipboard` → `SetClipboardData(CF_UNICODETEXT, h)`.
  - **Ownership via an explicit flag — not just the `SetClipboardData` result.** Track
    `bool osOwnsHandle = false` and set it `true` **only** immediately after a *successful*
    `SetClipboardData`. The OS takes ownership of `h` *only* on that success; **every** other
    exit path — `GlobalLock` returns `IntPtr.Zero`, `OpenClipboard`/`EmptyClipboard` fail, or
    `SetClipboardData` fails — leaves `h` unowned. In a `finally`: `if (!osOwnsHandle &&
    h != IntPtr.Zero) GlobalFree(h);` and always `CloseClipboard()` if the clipboard was
    opened. Persistent open failure → `ClipboardUnavailable`.
- **Returns:** `{ ok:true }`.

---

## 3. Threading

- **Grid/text reads** (`get_grid_cell`, `get_text`) resolve a `ref` and read a pattern →
  run on the **timeout-guarded transient STA** via a NEW
  `PerceptionManager.RunOnRefReadAsync` (transient STA + `RunActionAsync(func, timeoutMs)` +
  cache-free descriptor re-resolution), **not** the long-lived query STA. Rationale:
  `GridPattern.GetItem` and `TextPattern.GetText` can synchronously force the *target app*
  to realize containers / compute layout; the long-lived query STA has **no per-call
  timeout**, so a stalled provider would park it and starve all perception. The transient
  STA is abandonable and already carries the timeout machinery.
  - **Read path ≠ action path.** `RunOnRefReadAsync` is a sibling of the Phase-3a
    `RunOnRefActionAsync` that deliberately **omits** the hardcoded `IsOffscreen` →
    `ElementNotActionable` preflight. Reads must be allowed on off-screen elements (the
    snapshot already exposes off-screen nodes under `includeOffscreen`); forcing them
    on-screen would make targeted reads *stricter* than perception. (`grid_select`, a write,
    keeps `RunOnRefActionAsync` *with* the preflight, plus its own cell-offscreen check.)
  - **Shared in-flight cap.** Because reads now use the transient-STA dispatcher, they
    consume the same `MaxPendingActions` (=5) budget as actions (correct — a hung read parks
    a worker exactly like a hung action). A 6th concurrent targeted read/action →
    `TooManyPendingActions`. Clients should **serialize** bursts of targeted reads rather
    than firing them all at once.
  - (NB: a strict improvement, but it does not retire the *pre-existing* exposure that
    `SnapshotAsync`'s own tree walk runs on the query STA with no per-element timeout —
    separate hardening backlog, out of scope for 3b-2.)
- **`grid_select`** is a state change → the same Phase-3a **action** STA path
  (`RunOnRefActionAsync`), cache-free descriptor re-resolution, per-action transient STA,
  with the offscreen-cell preflight of §2.2.
- **Clipboard** uses raw `user32` P/Invoke, which is **apartment-agnostic** → it runs on a
  plain background `Task` (thread-pool/MTA). **No STA thread, no UIA automation.** (STA is
  required only for the OLE `IDataObject` clipboard wrappers, which we deliberately do not
  use.)

---

## 4. Components / files

- `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs` — add `ClipboardUnavailable`,
  `GridCellOutOfRange`.
- `src/FlaUI.Mcp.Core/Interaction/Interactor.cs` — add
  `GridSelect(AutomationElement grid, int row, int col)`.
- `src/FlaUI.Mcp.Core/Perception/PerceptionManager.cs` — add `RunOnRefReadAsync<T>` (the
  offscreen-tolerant transient-STA read sibling of `RunOnRefActionAsync`, see §3) and the two
  readers `GetGridCellAsync(handle, gridRef, row, col)` and `GetTextAsync(handle, ref,
  selectionOnly, maxLength)` built on it — both run the denylist check + `IsPassword`
  redaction.
- `src/FlaUI.Mcp.Core/Interaction/ClipboardAccess.cs` (new) — Win32 P/Invoke
  (`OpenClipboard`/`EmptyClipboard`/`GetClipboardData`/`SetClipboardData`/`GlobalLock`/
  `GlobalUnlock`/`GlobalAlloc`/`GlobalFree`/`GlobalSize`/`CloseClipboard`) run on a plain
  background `Task` (no STA, see §3); `GetTextAsync()`, `SetTextAsync(text)`.
- `src/FlaUI.Mcp.Server/Tools/ContentTools.cs` (new) — `desktop_get_grid_cell`,
  `desktop_grid_select`, `desktop_get_text`. ctor DI `(PerceptionManager, WindowManager,
  ServerOptions)`; `grid_select` uses the `Act → GuardWrite → RunOnRefActionAsync` helper.
- `src/FlaUI.Mcp.Server/Tools/ClipboardTools.cs` (new) — `desktop_clipboard_get`,
  `desktop_clipboard_set`. ctor DI `(ClipboardAccess, ServerOptions)`; `clipboard_set`
  through `GuardWrite`.
- `src/FlaUI.Mcp.Server/Program.cs` — register `ClipboardAccess`, `ContentTools`,
  `ClipboardTools` singletons.
- `test/FlaUI.Mcp.TestApp/MainWindow.xaml(.cs)` — add a `DataGrid` (named, populated),
  a multiline `TextBox` (named, seeded text), and a `PasswordBox` (named).
- `test/FlaUI.Mcp.Tests/**` — new Desktop-tagged tests + a non-Desktop error-code test.

---

## 5. Security / threat model

The sharp edge: `get_grid_cell` and `get_text` are **targeted reads that bypass
`SnapshotEngine`**. If they don't replicate its floor, an agent (or injected content) can
**end-run the snapshot block** by guessing a `ref` and reading targeted content from a
window the snapshot would refuse.

Mandate (enforced in the `PerceptionManager` read methods, before reading):
1. **Credential denylist** — check the resolved element's owning process via
   `PerceptionPolicy` (the same gate `SnapshotAsync` uses); denied → `TargetDenied`.
2. **`IsPassword` redaction** — if the target/cell `IsPassword`, return `[REDACTED]`,
   never the live value.

Clipboard:
- `clipboard_get` is a **global exfiltration surface**: a secret copied from a denylisted
  app (e.g. a password manager) is plain `CF_UNICODETEXT` with **no `IsPassword` context**
  — redaction is impossible at this layer. Mitigation = documented risk + an stderr
  audit-warn on every read. Clients should treat clipboard reads as sensitive.
- `clipboard_set` is a destructive global write → `destructiveHint:true` + `GuardWrite`.

Writes (`grid_select`, `clipboard_set`) carry `readOnlyHint:false, destructiveHint:true`;
human-in-the-loop confirmation remains the client's job (the server keeps tools granular).

---

## 6. Error handling

| Code | New? | Raised by |
| --- | --- | --- |
| `GridCellOutOfRange` | **new** | grid read/select: `row`/`col` outside `RowCount`/`ColumnCount`, or `GetItem` null |
| `ClipboardUnavailable` | **new** | clipboard get/set: `OpenClipboard` fails after retries |
| `PatternUnsupported` | reuse | element lacks Grid/Text/SelectionItem pattern |
| `ElementNotActionable` | reuse | cell realized but not selectable |
| `TargetDenied` | reuse | denylisted owning process on a targeted read |
| `WriteBlockedReadOnly` | reuse | a write tool in `--read-only-mode` |
| (ref-resolution codes) | reuse | unknown/stale `ref`, window mismatch (as today) |

Every error envelope keeps the existing non-null `suggestedRecovery`.

---

## 7. Testing

**Non-Desktop:** new error codes serialize by wire name (`GridCellOutOfRange`,
`ClipboardUnavailable`); read-only-mode blocks `grid_select` + `clipboard_set`.

**Desktop-tagged (run in bounded chunks per the known under-load limit):**
- grid: read a known cell value; `grid_select` selects it (verify via snapshot/selection);
  out-of-range row/col → `GridCellOutOfRange`; an off-screen (scrolled-out) cell → `grid_select`
  yields `ElementNotActionable` (offscreen preflight).
- text: read the multiline `TextBox` (full + `selectionOnly`); `maxLength` truncates and
  sets `truncated`; reading the `PasswordBox` → `[REDACTED]`.
- security: a targeted read against a denylisted process (simulated policy) → `TargetDenied`.
- clipboard: `clipboard_set` then `clipboard_get` round-trips text; empty clipboard →
  `{text:""}`.

---

## 8. Sequencing (reads before writes)

1. Error codes (`GridCellOutOfRange`, `ClipboardUnavailable`).
2. TestApp targets (DataGrid, multiline TextBox, PasswordBox).
3. `desktop_get_grid_cell` (read + security).
4. `desktop_get_text` (read + security + `maxLength`).
5. `Interactor.GridSelect` + `desktop_grid_select` (write).
6. `ClipboardAccess` + `desktop_clipboard_get` (read).
7. `desktop_clipboard_set` (write).
8. Docs (README/ROADMAP), version bump `0.4.0`→`0.5.0` (csproj + installer `.iss`), wrap.

---

## 9. Open questions / notes

- **Cell value source.** `Value.Value` then `Name` fallback covers DataGrid/list cells;
  some grids expose cell text only via `TextPattern`/`LegacyIAccessible` — acceptable
  v0.5.0 limit, documented (agent can `desktop_snapshot rootRef` for awkward grids).
- **`maxLength` default 10000** is a guess at a safe context budget; revisit with dogfooding.
- **Query-STA snapshot exposure (pre-existing, out of scope).** `SnapshotAsync` walks the
  target tree on the long-lived query STA with no per-element timeout, so a hung provider
  can park it. 3b-2 moves its *own* targeted reads off the query STA (§3); retrofitting the
  full snapshot walk with a timeout is separate hardening backlog.
- **AGY-AFTER review — TWO rounds folded (web relay, agy bus dead).** Round 1: clipboard
  Win32 memory protocol pinned (GMEM_MOVEABLE / single wide-null / ownership transfer — §2.5);
  `grid_select` offscreen-cell preflight (§2.2); `get_text` `maxLength` off-by-one +
  brittle-`GetSelection` try/catch + password short-circuit (§2.3); clipboard moved off STA
  to a plain `Task` (§3); empty-string set fast-path (§2.5); `PatternUnsupported` names the
  cell pattern (§2.2). Round 2 (confirm-pass, all R1 fixes verified): targeted reads use a
  new offscreen-tolerant `RunOnRefReadAsync` (§3) — NOT `RunOnRefActionAsync`, whose
  hardcoded offscreen throw would make reads stricter than snapshot; `get_text` gains
  `isPassword` in its payload to disambiguate a masked field from literal `"[REDACTED]"`
  (§2.3); the clipboard ownership rule is an explicit `osOwnsHandle` flag covering *all*
  intermediate-failure paths, not just `SetClipboardData`'s result (§2.5); targeted reads
  share the `MaxPendingActions`=5 in-flight cap (§3, documented). No agy claim was fabricated
  in either round; R1's "double-null" wording was corrected to a single wide-null.
