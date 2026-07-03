# FlaUI.Mcp — Phase 7: `desktop_paste_text` (atomic clipboard-preserving paste)

**Status:** approved design (2026-07-03); reworked after AGY-AFTER panel rounds 1 (NO-GO → folded
Seats 1–4) and 2 (GO → folded containment gate, rich-text degradation, force-audit). Supersedes the
"clipboard-paste remedy → automatic, discoverable path" item in the ROADMAP consumer-lens backlog.
**Branch:** `phase-7-paste-text` (off `master` @ v0.7.6 / `447ea13`).

## 1. Problem

Reactive editors garble real synthetic keystrokes (`SendInput`) at **any** inter-key pacing:
the new Windows 11 Notepad (RichEdit + autocomplete) and Chromium-family editors
(Monaco / CodeMirror / `contenteditable`). This is live-validated (v0.7.1 caveat, v0.7.2 probe).
The reliable insert paths are UIA `ValuePattern` (`desktop_set_value`) **when the target exposes a
writable ValuePattern**, and otherwise **clipboard paste**. Chromium `contenteditable` typically
returns `PatternUnsupported` from `set_value`, leaving **clipboard paste as the only reliable path**.

Today that paste path is a **manual, non-atomic two-step**: `desktop_clipboard_set(text)` then
`desktop_key("Ctrl+V")`. Two problems:

1. **Not discoverable / not atomic.** The agent must know the idiom and issue two coupled calls.
2. **Clobbers the user's clipboard.** `desktop_clipboard_set` overwrites whatever the human had
   copied, with no restore.

The ROADMAP consumer-lens backlog (v0.7.3 capstone) names the fix: "Promote the clipboard-paste
remedy from documented-workaround toward an **automatic, discoverable path**." Phase 7 delivers that
as a single tool.

## 2. Goal / non-goals

**Goal:** one tool, `desktop_paste_text`, that atomically pastes text into a focused element via a
clipboard-backed `Ctrl+V`, gated by the same synthetic-input safety pipeline as every other input
tool, with the same soft typed-text `verify` as `desktop_type`, that **never silently destroys or
silently degrades** the user's clipboard and **never leaks** the user's prior clipboard into the
document.

**Non-goals (explicitly out of scope):**
- **Full-fidelity clipboard preservation.** The `ClipboardAccess` layer is CF_UNICODETEXT-only.
  Rich formats (HTML/RTF) and binary formats (images, dropped files) are **not** round-tripped. The
  tool refuses (pure non-text) or reports degradation (mixed text+rich) rather than pretend success —
  no fragile OLE/delayed-render capture here.
- **Alternate paste chords.** `Ctrl+V` only. No `Ctrl+Shift+V` / `Shift+Insert` override in v1 (YAGNI).
- **Auto-strategy selection inside `desktop_type`.** Phase 7 keeps tools discrete/auditable (a
  deliberate doctrine). `desktop_type` continues to *advise* a remedy tool on verify mismatch; it
  does not silently switch mechanisms. Folding type/paste/set_value into one polymorphic pipeline
  was considered (agy) and rejected for v1 on auditability grounds.

## 3. Tool contract

```
desktop_paste_text(
    window:  string,               // window handle, e.g. "w1"
    ref:     string,               // element ref to focus and paste into, e.g. "e23"
    text:    string,               // text to paste (<= 1_000_000 UTF-16 units)
    timeoutMs: int  = 4000,        // per-STA block timeout
    verify:  bool   = true,        // read the element back and report match (soft; never throws).
                                   //   ALSO produces the consumption signal that gates restore (§5/§6).
    forceOverwriteClipboard: bool = false  // proceed even if the clipboard holds PURE NON-text content
                                           //   (image/files) that cannot be preserved (default: refuse). §5
) -> Destructive
```

- **`Destructive = true`** → blocked in `--read-only-mode` (`WriteBlockedReadOnly`; enforced by
  `ToolReadOnlyInvariantTests`).
- **Cap = 1,000,000 UTF-16 units.** Over cap → `InvalidArguments` (recovery: split on a whole-character
  boundary). Higher than `desktop_type`'s 4096 because paste pays no per-char `SendInput` cost. Empty
  `text` → `InvalidArguments` (nothing to paste; keeps a degenerate call from clobbering the clipboard).

### Success envelope (via `ToolResponse.Ok`)
```jsonc
{
  "ok": true,
  "pathUsed": "clipboard-paste",
  "clipboardRestored": "text" | "text-degraded" | "empty" | "abandoned" | "none-nontext",  // §5
  "verify": { ... VerifyResult ... }                                                         // §6
}
```
`clipboardRestored` is **always present** (a real enum string, never null). `verify` follows the
existing `VerifyResult` wire contract exactly (`VerifyResult.Disabled` when `verify=false`).

### New error code
- **`ClipboardHoldsNonText`** — the clipboard holds **pure** non-text content (image/files/HTML with
  no plain-text projection) that cannot be preserved, and `forceOverwriteClipboard` was `false`.
  Thrown **before** any clipboard mutation, so nothing is destroyed. Recovery prose names
  `forceOverwriteClipboard=true` as the opt-in. (Added to `ToolErrorCode`.)

## 4. Algorithm

`InputTools.DesktopPasteText`, wrapped in `ToolResponse.GuardWrite(_options, …)`. Ordering is
deliberate — **all refusal gates run before the clipboard is touched** (panel Seat 1-r1):

1. **Validate.** Empty `text` → `InvalidArguments` ("nothing to paste" — rejected so a degenerate call
   can't clobber the clipboard). `text.Length > 1_000_000` → `InvalidArguments`.
2. **Focus + before-read (one STA visit).** `_perception.RunOnRefForInputAsync(window, ref, (win, el) => …)`:
   `el.Focus()`, resolve `ActionTarget` via `InputTargeting.ResolveElementTarget(win, el)` (identity
   from the element, not the host window). If `verify`, capture the raw `before` text via
   `VerifyReader.FromElement(el)`. Same shape as `desktop_type` (InputTools.cs 91–98).
3. **Pre-authorize (NO clipboard mutation, NO budget consume).** `_guard.PreflightInput(target)` runs
   the refusal gates: elevation → **lease** → deny-list/interlock (`CheckTarget`) → session-state →
   **budget peek** (non-consuming). Throws the same `ToolException`s as a real send
   (`AccessDeniedIntegrity`, `InputNotLeased`, `TargetDenied`, `SinkInterlocked`,
   `InputDesktopUnavailable`, `InputBudgetExceeded`). A refused paste never clobbers the clipboard.
4. **Classify clipboard** (text-only, off-STA; §5). If `NonText` and `!forceOverwriteClipboard` →
   throw `ClipboardHoldsNonText` (still nothing destroyed).
5. **Set clipboard** to `text` via `ClipboardAccess.SetTextAsync(text)`. *(First mutation.)*
6. **Synthesize `Ctrl+V`** via `_guard.KeyChord(new[]{"Ctrl"}, "V", target)` on a `Task.Run`. Re-runs
   the **full** `InputGuard.Authorize` (idempotent re-check + budget **consume** + audit), then sends.
   Same gate as `desktop_type`/`desktop_key`; reuses the existing `KeyChordParser`/sink path.
7. **Confirm consumption + conditionally restore** (§5/§6). If `verify` (and a restore is pending),
   settle `VerifySettleMs` and read the element back once; the prior clipboard is restored **only if
   that read confirms the payload was consumed** (containment, §6), else the restore is forfeited and
   the agent payload is left on the clipboard (`"abandoned"`). No fixed settle timer is used for
   restore — the read is the gate (closes the Seat-2-r1 race).
8. Return `{ ok, pathUsed:"clipboard-paste", clipboardRestored, verify }`.

**Failure atomicity.** The only clipboard-mutating step is 5. If step 6 (`Ctrl+V`) throws (e.g. a
budget slot filled between the peek and the consume — a rare TOCTOU), the *agent's own* payload is
left on the clipboard and the tool throws. We deliberately do **not** early-restore on this path
(that would re-open the Seat-2-r1 race). Steps 3–4 guarantee the common refusals never reach step 5.

## 5. Clipboard model — classify, fail-fast on pure non-text, restore only on confirmed consumption

`ClipboardAccess` gains a classify capability:

```
enum PriorClipboardKind { Text, TextWithRichFormats, NonText, Empty }
record ClipboardSnapshot(PriorClipboardKind Kind, string? Text);  // Text non-null for Text & TextWithRichFormats
```

**Classify** by opening the clipboard once (existing `TryOpen` retry loop) and enumerating formats
(`EnumClipboardFormats`). Let `TEXT_SYNONYMS = { CF_UNICODETEXT(13), CF_TEXT(1), CF_OEMTEXT(7),
CF_LOCALE(16) }` (formats the OS auto-synthesizes from CF_UNICODETEXT):
- has CF_UNICODETEXT, **every** present format ∈ `TEXT_SYNONYMS` → **`Text`** (fully preservable; capture the string).
- has CF_UNICODETEXT, **some** present format ∉ `TEXT_SYNONYMS` (CF_HTML, RTF, CF_DIB…) → **`TextWithRichFormats`** (capture the plain string; rich part is not preservable). *(Seat-2-r2: prevents a rich clipboard silently classifying as pure `Text`.)*
- no CF_UNICODETEXT, ≥1 format present → **`NonText`**.
- no formats → **`Empty`**.

**Fail-fast vs. degrade-and-report:**
- **`NonText`** (image/files, no text projection) → nothing restorable. Default (`force=false`) →
  refuse `ClipboardHoldsNonText` before any mutation. `force=true` → overwrite; `"none-nontext"`; the
  override is **audit-logged** (`[audit] desktop_paste_text: force-overwrite of a non-text clipboard`).
  *(Seat-3-r2: the override is explicit + auditable, not a silent act.)*
- **`TextWithRichFormats`** → the plain text is restorable; formatting is not. This is **not** a
  refuse (rich clipboards are ubiquitous — any browser copy — and refusing would spam the agent).
  On confirmed consumption, restore the plain text and report **`"text-degraded"`** — honest that
  formatting was dropped, never silent.

**Restore is consumption-gated (Seat-2-r1, race-free):** restore only after the tool confirms the
target consumed the payload. Confirmation = a post-paste element read shows the field now **contains**
the payload it did not contain before (§6). Containment (not whole-field equality) makes the gate
reachable when pasting into a **non-empty** field (append/insert — the dominant coding case), so
restore is not limited to empty fields. *(Seats 1-r2 / 5-r2.)* Unconfirmed ⇒ forfeit restore, leave
the agent payload → `"abandoned"`.

| Prior kind            | consumption confirmed?            | Action                        | `clipboardRestored` |
| --------------------- | --------------------------------- | ----------------------------- | ------------------- |
| `Text`                | yes                               | `SetTextAsync(saved)`         | `"text"`            |
| `TextWithRichFormats` | yes                               | `SetTextAsync(saved plain)`   | `"text-degraded"`   |
| `Empty`               | yes                               | `SetTextAsync("")`            | `"empty"`           |
| `Text`/`Rich`/`Empty` | no (verify off / unconfirmed / read-failed) | leave payload       | `"abandoned"`       |
| `NonText`             | force=true (mutation happened)    | cannot restore                | `"none-nontext"`    |
| `NonText`             | force=false                       | refused pre-mutation (`ClipboardHoldsNonText`) | — |

- **`verify=false` ⇒ `"abandoned"` always** (no after-read ⇒ no confirmation). Honest + safe; the
  agent can restore manually via `desktop_clipboard_set` if it staged the prior text out-of-band.

New P/Invokes in `ClipboardAccess`: `EnumClipboardFormats(uint)` (+ `IsClipboardFormatAvailable` if
convenient). Existing strict OpenClipboard-retry / HGLOBAL-ownership discipline reused.

## 6. Verify + consumption gate (one after-read, two derived facts)

The **agent-facing `verify` RESULT** reuses `desktop_type` semantics **unchanged** (soft, never
throws; precondition short-circuits: `redacted` / `no-textpattern` / `field-not-empty` /
`empty→assert` / `read-failed`; on `empty→assert` it runs `TypedTextVerifier.Check`; mismatch carries
`canSetValue` + `recommendedFallbackTool`). Wire semantics of the `verify` object do not change.

The **restore consumption-gate** is derived from the **same** after-read by a **different predicate —
containment**: consumption is confirmed iff `after.Text` contains `text` (the payload) and
`before.Text` did not. Holds for empty fields (before `""` → after `==` payload) and non-empty fields
(before X → after contains payload) alike.

Read scheduling (verify=true):
- Do the focus/before-read as today.
- After `Ctrl+V`, settle `VerifySettleMs`(100) and perform **one** after-read via
  `RunOnRefReadAsync(readCapability:true)` whenever **either** (a) the verify precondition asserts
  (`before.Text==""`) **or** (b) a clipboard restore is pending (prior kind ∈ {Text,
  TextWithRichFormats, Empty}). In practice (b) is almost always true, so `verify=true` means one
  settle+read.
- **Agent-facing `verify` result** = existing precondition rules (e.g. `field-not-empty` still
  reported `Skipped`). **Restore gate** = containment on the same after-read. A redacted or failed
  after-read ⇒ cannot confirm ⇒ `"abandoned"`.
- The old fixed `PasteSettleMs(150)` is **removed**; the single `VerifySettleMs` before the after-read
  is the only settle, and restore never fires before the read observes the payload.

Edge — **empty payload** (`text==""`): **rejected upstream** with `InvalidArguments` (§4 step 1), so the
flow never sees an empty payload — this removes the degenerate "would clobber then abandon" case that a
containment gate can't confirm. *(Panel-plan-r2 Seat 1.)*

**Containment gate — accepted best-effort limitation.** Because confirmation is `after.Contains(payload)
&& !before.Contains(payload)`, restore reports `"abandoned"` (safe; payload left) when the pasted text
**already appears** elsewhere in the field, or when a reactive editor **transforms** it. Intentional:
restore is a courtesy, not a guarantee (the paste + the safety gates ARE guaranteed). Precise signal
(delayed-render `WM_RENDERFORMAT`) is the tracked **Phase 7.1** follow-up; do not weaken containment.

## 7. `desktop_type` remedy re-point

Re-point `desktop_type`'s verify-mismatch remedy for no-writable-ValuePattern targets at the new
atomic `desktop_paste_text`:
- `canSetValue == true`  → `recommendedFallbackTool = "desktop_set_value"` (unchanged).
- `canSetValue == false` → `recommendedFallbackTool = "desktop_paste_text"` (was: prose describing
  `desktop_clipboard_set` + `Ctrl+V`).
- `canSetValue == null` (read threw) → keep the safe default (`desktop_set_value`); name
  `desktop_paste_text` in the `remedy` prose as the contenteditable path.

Additive / backward-compatible `VerifyResult.From` / `RemedyProse` change (DTO already carries the
fields). Contract: the no-writable-ValuePattern branch's `recommendedFallbackTool` becomes
`desktop_paste_text`. Exact prose wording is a plan-level detail.

## 8. Safety & threat-model notes

- **All refusal gates precede the first clipboard mutation (Seat 1-r1).** `PreflightInput` at §4 step 3.
- **No prior-clipboard exfiltration (Seat 2-r1).** Restore is gated on confirmed payload consumption,
  so the target can never paste the user's prior clipboard (e.g. a copied password) because we
  restored it too early. Unconfirmed ⇒ leave the agent's own payload, never re-arm the secret.
- **No silent destruction / degradation (Seats 3-r1, 2-r2).** Pure non-text is refused before
  mutation unless `force=true` (audited); mixed text+rich is restored as plain text and reported
  `"text-degraded"`, never silently.
- **Same synthetic-input gate as all input.** Step 6 routes `Ctrl+V` through `InputGuard.Authorize`:
  credential/secure/UAC window → `TargetDenied`; interlocked shell → needs `shells` cap
  (`SinkInterlocked`); no lease → `InputNotLeased`; one budget slot consumed for the target window.
- **Audit.** `InputGuard.Authorize` records the `key` action for the `Ctrl+V`. The tool additionally
  emits event-only `[audit]` lines: a paste of N chars (count only, no content), and — when used —
  the `force=true` non-text override. Pasted text never traverses `SendInput`.

## 9. Testing

**Headless / pure (CI `Category!=Desktop`):**
- Cap (`>1_000_000` → `InvalidArguments`; boundary exactly 1_000_000 ok; empty ok).
- **`ClipboardSnapshot` classification** (via a seam/fake over the format probes, per existing
  `ClipboardAccessTests`): text-only synonyms → `Text`; CF_UNICODETEXT + CF_HTML → `TextWithRichFormats`;
  image-only → `NonText`; no formats → `Empty`.
- **Order / gate invariants (the panel folds):**
  - Refused preflight (no lease / denied) → clipboard set is **never** called *(Seat 1-r1)*.
  - `NonText` + `force=false` → `ClipboardHoldsNonText` **before** any set *(Seat 3-r1)*.
  - Restore only on confirmed containment; `verify=false` ⇒ `"abandoned"` and prior text **not**
    written back *(Seat 2-r1)*.
  - **Containment on a non-empty field:** before `"foo"`, payload `"bar"`, after `"foobar"` →
    consumption confirmed → prior restored *(Seats 1-r2/5-r2)*.
  - `TextWithRichFormats` + confirmed → `"text-degraded"` *(Seat 2-r2)*.
  - `force=true` non-text override emits the audit line *(Seat 3-r2)*.
- Verify precondition branches map to the correct agent-facing `verify` **and** the correct
  `clipboardRestored` (independent predicates).
- `desktop_type` remedy: `canSetValue==false` → `recommendedFallbackTool=="desktop_paste_text"`
  (extend `VerifyResultTests`).
- `ToolReadOnlyInvariantTests` auto-covers the new `Destructive` tool.

**Desktop / manual (run in an actively-connected interactive session):**
> `SendInput` is a *session-state* dependency, not an RDP one: it works in an actively-connected
> session (`WinSta0\Default`) — confirmed by the v0.7.0 Phase-4b spike over live RDP — and fails only
> when the session is minimized-without-`RemoteDesktop_SuppressWhenMinimized=2` or disconnected (the
> active desktop switches to the secure `WinSta0\Winlogon` lock screen). These tests are maintainer-run
> because **CI runs unattended (a locked/disconnected session)**, not because "RDP can't." A future
> interactive CI runner (ROADMAP backlog) should use **Sysinternals Autologon → unlocked physical
> console → CI agent as an interactive startup app (never a Session-0 service)**; `tscon /dest:console`
> is *not* sound (resolution collapse + WS2022/Win11 `tscon`-hijack hardening).

> **⚠ Pre-existing broken harness to fix first (found 2026-07-03).** The one real end-to-end `SendInput`
> test, `InputToolsTests.Type_writes_text_into_the_focused_textbox`, currently fails *before* any input —
> at `RefForAid(tree, "Input")` (`Sequence contains no matching element`): the WPF TestApp snapshot has no
> `aid=Input` node. It is a `[SkippableFact]` that otherwise just *skips* (no lease), so this has been
> latent. It is NOT a `SendInput` failure (injection was independently confirmed working over live RDP).
> Before the paste tool's Desktop tests can give a trustworthy green, investigate + fix this harness
> (stale TestApp tree? snapshot scope? renamed control) so `desktop_paste_text`'s Desktop suite doesn't
> inherit it. Track as a small task in the plan; it is a prerequisite for the interactive-CI item too.

- Live paste into the new Win11 Notepad and a Chromium `contenteditable`; multi-word string lands
  byte-for-byte where `desktop_type` garbles.
- Pre-seed a **text** clipboard, paste into a non-empty field → prior text restored (`"text"`).
- Pre-seed a **browser rich** clipboard → `"text-degraded"`. Pre-seed an **image** → default
  `ClipboardHoldsNonText`; `force=true` → `"none-nontext"`.

## 10. Files touched (anticipated — line-level plan follows in writing-plans)

- `src/FlaUI.Mcp.Core/Interaction/ClipboardAccess.cs` — `EnumClipboardFormats` (+maybe
  `IsClipboardFormatAvailable`) P/Invokes; `ClipboardSnapshot`/`PriorClipboardKind`; `Snapshot()`
  with the TEXT_SYNONYMS classification.
- `src/FlaUI.Mcp.Core/Interaction/InputGuard.cs` — `PreflightInput(ActionTarget)` (refusal gates
  minus budget-consume/audit); `ActionBudget` gains a non-consuming `HasFreeSlot(root, now)`.
- `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs` — add `ClipboardHoldsNonText`.
- `src/FlaUI.Mcp.Server/Tools/PasteFlow.cs` — **NEW** pure orchestrator (`PasteFlow.RunAsync` over an
  injected `IPasteEffects`): effect ORDER + gating + containment-restore + verify-outcome. This is the
  seam that makes the §9 safety invariants headless-testable (a recording fake). *(Panel-plan-r1 Seat 3.)*
- `src/FlaUI.Mcp.Server/Tools/InputTools.cs` — new `DesktopPasteText` tool = thin wiring: a real
  `IPasteEffects` (perception/guard/ClipboardAccess) handed to `PasteFlow.RunAsync`. No decision logic here.
- `src/FlaUI.Mcp.Server/Tools/VerifyResult.cs` (+`RemedyProse`) — remedy re-point.
- Tests: `ClipboardAccessTests`, `InputGuardTests`, `InputToolsTests`/`InputToolsContractTests`,
  `VerifyResultTests`.
- Docs/version: README tool list + skill note; `CHANGELOG` `[0.7.7]`; ROADMAP Phase 7 entry; csproj +
  installer bump (0.7.6 → 0.7.7). (Release cut is a separate user-gated step.)

## 11. Exhaustiveness self-audit (2026-07-03, post-panel-round-2)

- **Contract:** signature (incl. `forceOverwriteClipboard`), defaults, cap, envelope, every wire
  field (5-value `clipboardRestored`, `verify`), and `ClipboardHoldsNonText` all named. ✅
- **States enumerated:** 4 clipboard kinds × consumption → action → wire value as a full table (§5);
  verify precondition branches (5) fully listed. ✅
- **Ordering / atomicity:** refusal gates precede first mutation; the one residual TOCTOU clobber
  (budget slot fills between peek and consume) is called out + justified benign. ✅
- **Race closed:** fixed settle removed; restore consumption-gated by containment (§6). ✅
- **Rich-text honesty:** mixed text+rich → `"text-degraded"`, never silent plain-text degradation. ✅
- **Restore reachability:** containment gate makes restore work on non-empty fields, not just empty. ✅
- **Backward-compat:** `desktop_type` remedy additive; `canSetValue null` default preserved. ✅
- **Open items deferred to the plan (not gaps):** exact `ClipboardAccess`/`InputGuard` method+record
  names; exact `RemedyProse` wording; empty-payload consumption handling; test seam vs. fake. All
  line-level, not design forks.
- **No placeholders / TBD remain.** ✅

### Panel provenance
- **Round 1 (NO-GO → folded):** Seat 1 auth-before-clobber ordering; Seat 2 password-paste settle
  race; Seat 3 non-text silent loss → fail-fast + `force`; Seat 4 `"abandoned"` wire state.
- **Round 2 (GO → folded):** Seat 1/5 restore-reachability → containment gate; Seat 2 rich-text
  silent degradation → `TextWithRichFormats`/`"text-degraded"`; Seat 3 force-loop → audited override.
