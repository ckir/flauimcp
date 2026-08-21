# Occlusion-Aware Window Capture (`PrintWindow`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture a window's own pixels regardless of occlusion, without touching focus, with redaction masks still landing on the right regions and no wrong image ever returned unlabelled.

**Architecture:** Window and element scope stop scraping the screen and acquire through `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`. Full-desktop and OCR keep the scrape. A new `WindowCaptureCoordinator` owns the retry loop, the bookend mask-validation walk and the scrape fallbacks; a new `ScreenCapture.CaptureWindow` seam owns acquisition, the `W2`-side guards and the crop; acquisition sits behind `IWindowImageSource` so everything except the interop is headless-testable.

**Tech Stack:** C# / .NET 10 (`net10.0-windows10.0.19041.0`), xUnit, `System.Drawing`, FlaUI, Win32 interop (`user32`/`gdi32`).

---

## READ THIS BEFORE TASK 1

**Source spec:** `docs/superpowers/specs/2026-08-21-occlusion-aware-capture-design.md`, **at HEAD of this branch**. Read §1, §2, §2.5, §2.6, §4 and §5 before starting.

⚠ **Do not read the spec at `6ab0087`** — that is the pre-ratification version and three of its rules are superseded. The spec has ALSO been amended twice since the ratification commit `b11b87b`, both times because the panel over THIS PLAN found a defect the plan had faithfully inherited from it (`uniformCanvas`'s scope, and "a fallback scrape gets neither"). Always read it at HEAD.

**Five things this plan carries that re-deriving from the panel record would undo:**

1. **§2.5 and §2.6 did not exist when the panel ran.** They were added by operator ratification on 2026-08-21 and have had **no adversarial review at all**. They are the newest and least-attacked ideas in the design. Treat every claim in them as unproven.
2. **Window scope RETRIES before refusing** (ratification item 2). The panel-era rule refused on the first size mismatch. Do not restore it.
3. **The hung-window leak is staged** (ratification item 3): measure first; the containments ship, the out-of-process worker does not.
4. **`CaptureRectangle` has THREE other callers, not one.** `ScreenshotTools.cs:49`, `FindTextTools.cs:62`, `FindTextTools.cs:110`. Risk 6 in the spec said "one" until this plan corrected it.
5. **Two measurements gate the design, not just its tuning.** Tasks 1 and 2 come first for that reason, and Task 3 is a hard stop.

**Repo rules that will fail the build if ignored:**

- **Warnings are errors repo-wide** (root `Directory.Build.props`, since `54b1dc5`). Every build must be 0 warnings / 0 errors.
- **Never pass `--no-build` to `dotnet test`.** A deleted test still runs from the stale DLL.
- The solution is **`FlaUI.Mcp.slnx`**. There is no `.sln`.
- **Every new gate needs a logic mutant** proving that *specific* test goes red. A structural break (deleting a property) proves only that the symbol was referenced.
- `CaptureResult` and `CaptureGeometry` are **positional records**: **append fields, never insert**.

**Branch:** `item8-occlusion-aware-capture`, already created from master `6ab0087`. Spec amendment is `b11b87b`. Nothing is pushed this release.

**Two properties this plan STATES rather than lets you discover.** Neither is a defect and neither has a task; they are here because finding them mid-implementation would look like one.

1. **The width ceiling bounds the PAYLOAD, never the ALLOCATION** (spec risk 7). `MaxCaptureWidth = 1920` (`ScreenCapture.cs:17`) is applied inside `Encode` (`:47-50`), *after* the source bitmap exists. Under the scrape that changed nothing, because the scrape only ever grabbed the rectangle asked for. Under `PrintWindow` the allocation is dictated by the **window's own size**, so a 4K-wide window is a ~33 MB managed bitmap plus its GDI twin, per capture, in a long-lived server. `maxWidth` does not reduce it. If you are looking at memory during Task 23 and see a large transient, this is why.

2. **DPI already lines up, and depends on the manifest.** The server is `PerMonitorV2` DPI-aware (`src/FlaUI.Mcp.Server/app.manifest:5`), so `GetWindowRect` returns physical pixels that match `geo.Bounds` and the `W1`/`W2` comparison is apples-to-apples. Measured: run DPI-aware the probe's sizes matched `desktop_list_windows` exactly; run DPI-*un*aware they were scaled to 80%. **Do not remove or weaken that manifest entry** — every rectangle in this design silently becomes wrong if it goes.

---

## File Structure

**New files — `src/FlaUI.Mcp.Core/Perception/`**

| File | Responsibility |
|---|---|
| `CaptureScope.cs` | The four-value scope enum both seams branch on |
| `CaptureWarning.cs` | The `{code, recourse}` pair, the seven code constants, and their recourse text |
| `CaptureOutcome.cs` | The **four**-case seam outcome: completed / resized / timed out / target-transient |
| `WindowCropGeometry.cs` | The PURE crop function — rectangles in, rectangles out, no handles, no pixels |
| `IWindowImageSource.cs` | The injectable acquisition seam |
| `UniformCanvasDetector.cs` | The uniform-colour predicate and its sampling strategy |
| `WindowCaptureCoordinator.cs` | Steps 1–9: the walk, the retry loop, the bookend walk, the guarded fallbacks, and `CaptureCircuitBreaker` |

**New files — `src/FlaUI.Mcp.Server/Capture/`**

| File | Responsibility |
|---|---|
| `PrintWindowImageSource.cs` | The real interop. The ONLY Desktop-category production file in this feature |

**Modified**

| File | Change |
|---|---|
| `src/FlaUI.Mcp.Core/Perception/ScreenCapture.cs:9` | `CaptureResult` gains two appended fields |
| `…/ScreenCapture.cs:36` | `CaptureRectangle` gains `scope` + `warningsSoFar` |
| `…/ScreenCapture.cs:45` | `Encode` gains `reported`, `method`, `warnings`; `captureBounds` renames to `absolute` |
| `…/ScreenCapture.cs:11-14` | Class doc loses the occlusion + never-black claims |
| `…/PerceptionManager.cs:852` | `ResolveWindowCaptureGeometryAsync` gains `clipToVirtualScreen` |
| `…/PerceptionManager.cs:915-935` | The degenerate-`W1` guard, between the bounds read and the yardstick |
| `…/PerceptionManager.cs:935` | The yardstick becomes conditional |
| `…/PerceptionManager.cs:1275` | `CaptureGeometry` gains three appended fields + a `HasPopupRoots` init property |
| `…/PerceptionManager.cs:1134-1144` | `ResolveTextCaptureGeometryAsync` — the OCR path's degenerate-window refusal |
| `src/FlaUI.Mcp.Core/FlaUI.Mcp.Core.csproj` | `InternalsVisibleTo` for the tests, if it is not already there (Task 9 Step 1) |
| `src/FlaUI.Mcp.Server/Tools/ScreenshotTools.cs:17` | Tool description: three separate edits |
| `…/ScreenshotTools.cs:49,58,71-84` | Call sites and the metadata projection |
| `src/FlaUI.Mcp.Server/Tools/FindTextTools.cs:62,110` | The third caller — explicit scope, explicit yardstick |
| `src/FlaUI.Mcp.Server/Program.cs` | DI for `IWindowImageSource`; the audit-signal flag |
| `src/FlaUI.Mcp.Server/ServerOptions.cs` | The audit-signal flag |
| `src/FlaUI.Mcp.Server/Capture/CaptureAuditSignal.cs` | The occlusion audit signal (§2.6), flag-gated OFF |
| `ROADMAP.md` | **Item 17** (the out-of-process worker) — added by Task 19 Step 7. ⚠ **Item 18** (the OCR path's denylist hole) is ALREADY COMMITTED, during the plan's panel; no task adds it and none should |

---

## The plan, by phase

⚠ **This file is the INDEX. The tasks live in the files below** — it was split at ~5,000 lines because a
plan nobody can hold in context is a plan that gets skimmed. **Everything above this line still applies to
every task**, and the ordering between phases is not negotiable: Phase 0 gates the design, and Task 3 is a
hard stop.

| Phase | Tasks | File | What it settles |
|---|---|---|---|
| **0** | 1–3 | [Measurements](2026-08-21-occlusion-aware-capture/phase-0-measurements.md) | **Two answers can change the design. Task 3 is a HARD STOP.** No production code until the operator has seen all three. |
| **1** | 4–7 | [Contract types](2026-08-21-occlusion-aware-capture/phase-1-contract-types.md) | `CaptureScope`, `CaptureWarning` + the **six** wire codes, `CaptureResult`'s two appended fields, `CaptureOutcome`'s four cases |
| **2** | 8–10 | [Crop and encode](2026-08-21-occlusion-aware-capture/phase-2-crop-and-encode.md) | The PURE crop geometry, `Encode`'s two rectangles, and `CaptureRectangle`'s scope — including the OCR third caller |
| **3** | 11–13 | [Detector, yardstick, walk](2026-08-21-occlusion-aware-capture/phase-3-detector-yardstick-walk.md) | The uniform detector, the mask-preserving yardstick default, and `CaptureGeometry`'s `W1` / `HWND` / degeneracy signal |
| **4** | 14–16 | [Acquisition seam](2026-08-21-occlusion-aware-capture/phase-4-acquisition-seam.md) | `IWindowImageSource`, `CaptureWindow`, and the only file in this feature that touches Win32 |
| **5** | 17–19 | [Coordinator](2026-08-21-occlusion-aware-capture/phase-5-coordinator.md) | The retry loop, the ratified resize policy, the bookend validation walk, the fallbacks and the circuit breaker |
| **6** | 20–25 | [Wiring and gates](2026-08-21-occlusion-aware-capture/phase-6-wiring-and-gates.md) | `ScreenshotTools`, the metadata projection, the audit signal, the docs, and every gate |
| — | — | [Panel record](2026-08-21-occlusion-aware-capture/panel-record.md) | Every AGY-AFTER round: what was folded, and **what was refuted by measurement** |

⚠ **Read the [panel record](2026-08-21-occlusion-aware-capture/panel-record.md) before re-raising
anything.** It lists the claims already refuted by measurement, and several of them are the obvious-looking
objections a fresh reader reaches first.
