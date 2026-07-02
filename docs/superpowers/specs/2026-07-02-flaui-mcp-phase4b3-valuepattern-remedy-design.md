# Phase 4b.3 — ValuePattern-aware verify remedy (design)

**Date:** 2026-07-02
**Status:** approved (design) — pending spec review → plan
**Version target:** 0.7.5 (additive minor: new wire fact + smarter remedy; no breaking change)

## Problem

`desktop_type`'s soft `verify` step, on a garble into an initially-empty field,
returns `verify{ mismatch:true, expected, actual, recommendedFallbackTool, remedy }`.
Today the remedy is **hardcoded** to `desktop_set_value` (UIA ValuePattern). But
`desktop_set_value` returns `PatternUnsupported` on a target with **no ValuePattern**
(e.g. an Electron `contenteditable`, which exposes TextPattern for *reading* but no
ValuePattern for *writing*). On those targets the advice is a dead end; the correct
recovery is the clipboard-paste path (`desktop_clipboard_set` → focus → `Ctrl+V`).

## Decision (folds AGY-FIRST consult, 2026-07-02)

Emit the **capability fact** on the wire and let the agent self-route, rather than
having C# imperatively pick one (possibly-wrong) remedy:

1. **Read the fact — inside the after-read's STA visit, on the already-resolved
   element (no extra resolution, no tree re-traversal).** The capability predicate for
   "writable via `desktop_set_value`" is `Patterns.Value.IsSupported && !Value.IsReadOnly`
   (a read-only ValuePattern, IsReadOnly=true, is NOT writable ⇒ false). **The try/catch
   MUST envelop the WHOLE expression, not just `IsSupported`** — evaluating
   `!Value.IsReadOnly` dereferences the ValuePattern and makes its own cross-process COM
   read, which can throw `ElementNotAvailableException`/`COMException` independently
   (e.g. the window closes mid-check). Any throw ⇒ **unknown** (`null`).
   **Where it runs — the convergence of AGY-AFTER R2 (don't tax the hot path) and R3
   (don't re-resolve):** the capability is read within the SAME after-read STA lambda
   that already resolves the element and reads its TextPattern, reusing that live
   element — NOT in a separate `RunOnRefReadAsync` (a second full tree resolution;
   ~10-100ms; R3 Ops) and NOT by holding the element to read later off-thread (illegal —
   a UIA `AutomationElement` is COM-bound to its resolving STA). It is **gated to the
   after-read only** (the before-read at DesktopType's focus step does NOT read it), so
   the cost is ~2 extra COM property calls on an element already being visited, only on
   the empty-field assert path — negligible, and the common Match still pays only those
   2 marginal calls (far below R2's "wasted full read" bar). The capability is discarded
   unless `TypedTextVerifier.Check` later returns `Mismatch`.
   (NOTE: `desktop_type` has no coordinate path — it ALWAYS takes a `ref` — so there is
   no ref-less execution path to special-case; R3's "coordinate-typing hole" does not
   exist in this tool.)
2. **Carry it.** A small leaf `ValueCapability.CanSetValue(AutomationElement) -> bool?`
   (live-UIA, defensive, whole-expression try/catch) invoked inside the after-read
   lambda. `VerifyRead` gains a `bool? CanSetValue`, populated ONLY on the after-read
   (the before-read leaves it `null`). `true` = writable via set_value; `false` = not
   writable (no ValuePattern OR read-only); `null` = couldn't determine / not read.
   (Password/redacted never reaches the remedy branch — it short-circuits to Skipped
   upstream.)
3. **Map it on the wire** (in `VerifyResult.From`, the Mismatch arm — the only arm
   that emits a remedy):
   - Add wire field `canSetValue` (`bool?`, JsonIgnore-when-null ⇒ absent when
     unknown). This is the *fact* the agent reasons from. **Named for what the
     predicate actually computes** — "can `desktop_set_value` succeed" — NOT
     `hasValuePattern`: an element that *has* a read-only ValuePattern is `canSetValue:false`,
     so a "has" name would be a semantic lie (wire-contract seat, AGY-AFTER R1).
   - `recommendedFallbackTool` (best-effort single-key hint, backward-compatible):
     `true → "desktop_set_value"`, `false → "desktop_clipboard_set"`,
     `null → "desktop_set_value"` (**safe default** — a wrong set_value guess yields
     a clean `PatternUnsupported` the agent recovers from, whereas defaulting to the
     clipboard path would **unconditionally clobber the user's clipboard**; per agy's
     Fork-B correction, never default a destructive side-effect on uncertainty).
   - `remedy` prose becomes **strategy-listing** (not a single imperative) and MUST
     name the EXACT tools so the consuming agent cannot hallucinate a nonexistent
     `desktop_paste` (UX/operator seat, AGY-AFTER R1). Verbatim intent:
     *"Strategy 1 (canSetValue:true): `desktop_set_value` for byte-exact entry.
     Strategy 2 (canSetValue:false, e.g. an Electron contenteditable with no
     ValuePattern): `desktop_clipboard_set` with the ORIGINAL full text you intended
     to type — do NOT pipe the `expected` field from this result, it is a TRUNCATED
     echo — then `desktop_key` \"Ctrl+V\" targeting this element's ref+window (which
     focuses it first): a 2-call sequence, not a 3-step dance. Unknown/absent: try
     `desktop_set_value` first, fall back to Strategy 2 on `PatternUnsupported`."*
     (Ctrl+V into a terminal element still needs the `shells` lease cap.)

`TypedTextVerifier` stays **pure and untouched** (ValuePattern support is a live-UIA
fact, not typed-text logic). The branch lives entirely in the Server-layer mapper
`VerifyResult.From`, which is headless-testable given a `bool? canSetValue`.

**Feature-detection / JsonIgnore-when-null (open call for the review gate).** The new
`canSetValue` uses JsonIgnore-when-null, so an *unknown* fact is an ABSENT key. The
wire-contract seat (AGY-AFTER R2) argued for serializing an explicit `canSetValue: null`
so a consumer can distinguish a pre-0.7.5 server (no feature) from a 0.7.5 server whose
UIA read threw. Assessment: kept JsonIgnore-when-null because (a) the primary consumer
is an LLM routing off the `remedy` prose, not a strict schema client; (b) real feature
detection is served out-of-band by the MCP server version and by
`recommendedFallbackTool: "desktop_clipboard_set"` being a value pre-0.7.5 never emitted;
(c) "explicit null on mismatch but absent on Match/Skipped" fights a single flat property.
Reversible in the plan if the user prefers the explicit-null contract.

## Wire contract (verify object on mismatch)

```
verify: {
  ran: true, verified: false, mismatch: true,        // always present
  expected: "...", actual: "...",                     // truncated echoes (unchanged)
  canSetValue: true | false,                          // NEW; omitted when unknown
  recommendedFallbackTool: "desktop_set_value" | "desktop_clipboard_set",
  remedy: "<strategy-listing prose, names exact tools>" // opaque, wording not stable
}
```
Non-mismatch arms (Match/Skipped/Disabled) are unchanged — no `canSetValue`,
no remedy. `ran/verified/mismatch` remain always-present; new key is JsonIgnore-when-null.
Additive only: existing consumers that ignore unknown keys are unaffected.

## Components & boundaries

| Unit | Change | Tested |
|---|---|---|
| `ValueCapability.CanSetValue` (Core) | NEW leaf: read `Value.IsSupported && !IsReadOnly` (whole expr in try/catch), defensive → `null` | live smoke only (touches UIA) |
| `VerifyReader.FromElement` (Core) | + optional `bool readCapability` param: when true, also calls `ValueCapability.CanSetValue(el)` on the SAME live element and returns it on `VerifyRead.CanSetValue`. Before-read passes false; after-read passes true. | live smoke only |
| `VerifyRead` (Core) | + `bool? CanSetValue` (populated only on the after-read) | — |
| `VerifyResult` (Server) | + `CanSetValue` wire field (JsonIgnore-when-null); `From(VerifyOutcome, bool? canSetValue = null)`; branch mismatch arm; new prose | **headless unit** (VerifyResultTests) |
| `InputTools.DesktopType` (Server) | before-read: `FromElement(el, readCapability:false)`; after-read: `FromElement(el, readCapability:true)`. Final call passes `after.CanSetValue`: `From(outcome, after.CanSetValue)`. No extra resolution. | — |

## Error handling

- ValuePattern read throws (evaluated inside the after-read's try/catch on the live
  element) ⇒ `canSetValue = null` ⇒ `canSetValue` key omitted + safe `desktop_set_value`
  hint + prose covers the unknown case. `verify` still never throws and never converts a
  successful type into a failure.
- Redacted/no-TextPattern/field-not-empty still short-circuit to Skipped upstream (no
  remedy, no leak — the capability is read but discarded when the outcome isn't Mismatch).
  Unchanged.

## Testing

- **Headless unit (VerifyResultTests):** `From(mismatch, canSetValue)` for
  `true` → tool `desktop_set_value`, `canSetValue:true`; `false` → tool
  `desktop_clipboard_set`, `canSetValue:false`; `null` → tool `desktop_set_value`,
  `canSetValue` absent (JsonIgnore-when-null). Assert remedy prose non-empty and names
  both `desktop_set_value` AND `desktop_clipboard_set`+`desktop_key`. Non-mismatch arms
  unchanged (no canSetValue, no remedy).
- **Filter:** `Category!=Desktop` (headless box). Live ValuePattern read exercised in
  the documented smoke, not headless.
- **Regression floor:** existing VerifyResult/TypedTextVerifier tests stay green.

## Docs / release

- `VerifyResult` XML contract note updated (new field + best-effort semantics).
- `README` verify note + `.claude/skills/driving-flaui-mcp/SKILL.md` (its gotchas
  table already states the set_value-vs-clipboard rule; add the `canSetValue`
  wire-fact mention).
- Version `0.7.4 → 0.7.5` (Server csproj + installer.iss), CHANGELOG `[0.7.5]`,
  ROADMAP Phase 4b.3 entry. (Whether/when to cut the release is a separate decision.)

## Deferred (not this fork)

- **`desktop_paste_text` atomic tool** (agy Reframe 1): save clipboard → focus →
  Ctrl+V → restore clipboard, so the clipboard path is one clean tool with no clobber.
  Good idea but a new tool (lease/deny-list/tests/docs) and clipboard restore is only
  reliable for *text* (rich formats/images/delayed-render are lost) — deserves its own
  scoped spec. Captured here as a follow-on candidate.

## Out of scope

Changing `desktop_set_value`/`desktop_clipboard_set` themselves; auto-executing the
fallback (verify stays advisory — never acts, never throws).
