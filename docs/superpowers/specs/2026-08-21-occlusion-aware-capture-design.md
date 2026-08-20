# Occlusion-aware window capture (`PrintWindow`) — design

**ROADMAP item 8.** Second of four in the v1.0.0 batch, after the release-tooling subproject merged
(`128e7d3`).

## The problem

`desktop_screenshot` at window scope captures the window's **screen rectangle**:

```csharp
// ScreenshotTools.cs:58
result = await Task.Run(() => ScreenCapture.CaptureRectangle(geo.Bounds, geo.MaskRects, maxWidth));
```

`CaptureRectangle` acquires with `Capture.Rectangle(absolute, null)` — a scrape of that screen region. So
capturing a background window returns **a photograph of whatever occludes it**, confidently and silently.
Nothing detects it.

There is no focus-first code. The tool description says *"Focus the window first (no occlusion
handling)"* — that is an instruction to the CALLER, not behaviour in the product. An instruction the
caller must remember is the same shape as the README line that told maintainers to disable a plugin id
that no longer existed: it works until someone doesn't.

**Goal:** capture a window's own content regardless of occlusion, without touching focus.

**Success criterion, checkable:** capture a window that is behind another window and get that window's
pixels, with redaction masks still landing on the right regions, and with no case where a wrong image is
returned as if it were correct.

## Evidence — MEASURED, not assumed

Probe `.clavity/scratch/printwindow-probe/probe.ps1`, run DPI-aware on BOTH an RDP session and the
physical console. "non-black" is the fraction of a 40x40 sample grid that is not near-black.

| Window | Class | flag=0 | flag=2 (`PW_RENDERFULLCONTENT`) |
|---|---|---|---|
| WindowsTerminal | DirectX/Atlas | 0 | 0.951 |
| explorer (small) | plain Win32 | 1 | 1 |
| SecHealthUI, **occluded** | XAML/UWP | 0 | 0.044 |
| PowerToys.QuickAccess | WPF | 0.025 | 0.975 |
| Program Manager | shell | 0 | 0.29–0.34 |

**F1. `PW_RENDERFULLCONTENT` (flag 2) is mandatory.** Flag 0 returns blank for four of five windows.

**F2. The BOOL return is worthless as a success signal.** `ret=True` on every call, including every
all-black one. Whatever failure handling this design chooses, it cannot be driven by the return value.

**F3. Console and RDP agree exactly** — 0.951 / 1 / 0.044 / 0.975 identical in both. The one differing
row is a display-resolution change (1920x1080 → 1366x768), not a transport difference. So the mechanism
does not depend on the session type, and the Desktop-suite console requirement adds nothing here.

**F4. A darkness-based blank detector is UNSOUND, and this was proven rather than reasoned.** The 0.044
row looked like a failure. The PNG was dumped and inspected: it is a pixel-perfect render of the occluded
window — nav rail, toggles, body text, the "Real-time protection is off" warning. 0.044 is a DARK THEME.
A darkness heuristic would have rejected a flawless capture.

**F5. Captures exceed the screen.** That 1920x1020 window was captured in full while the physical display
was 1366x768. `PrintWindow` returns pixels that are on no monitor. This is the fact that forces the
yardstick change below.

## The change

### 1. Acquisition

Window scope and window+ref element scope acquire via `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`,
**replacing** the scrape. Full-desktop scope keeps the scrape — `PrintWindow` is inherently per-window and
has no full-desktop analogue.

This introduces the repo's first native interop of this kind: `grep -rn "PrintWindow|BitBlt|GetWindowDC"
src/` currently returns nothing.

**Masking is untouched.** `ScreenCapture.Encode` translates mask rects by `clip.X - captureBounds.X`
(`ScreenCapture.cs:61-62`), keyed on `captureBounds` and not on the acquisition backend. Acquisition
(`Capture.Rectangle`) and masking (`Encode`) are a clean seam; only the former changes.

The server is `PerMonitorV2` DPI-aware (`src/FlaUI.Mcp.Server/app.manifest:5`), so `GetWindowRect` returns
physical pixels that match today's `geo.Bounds`. The probe reproduced this: run DPI-aware, its sizes
matched `desktop_list_windows` exactly (1920x1020, 400x516); run DPI-unaware they were scaled to 80%.

### 2. The yardstick correction — a leak if skipped

`PerceptionManager.cs:930-947` computes:

```csharp
var yardstick = System.Drawing.Rectangle.Intersect(captureBounds, ScreenCapture.VirtualScreenBounds());
```

and then early-returns when that intersection is empty, on this stated premise:

> ⚠ A window with NO renderable overlap contributes no pixels to any capture, so there is nothing here to
> withhold and nothing to refuse over.

**F5 falsifies that premise under `PrintWindow`.** Pixels outside the virtual screen ARE captured. Left
unchanged, the early return would drop the entire mask set for a window whose pixels are in the image —
a leak of exactly the class SP4 existed to close.

**Under `PrintWindow` the yardstick is the UNCLIPPED `captureBounds`.** The clipping exists to stop a
maximized window's invisible resize-border bleed from defeating the blacks-out check; that reasoning is
specific to a scrape, where off-screen pixels genuinely cannot appear.

⚠ The two acquisition paths now need DIFFERENT yardsticks — clipped for the full-desktop scrape,
unclipped for `PrintWindow`. The method cannot infer which caller it serves; that is already true of the
`skipIfNoRenderableOverlap` parameter beside it, and the plan follows that existing precedent rather than
inventing a new mechanism.

### 3. Failure policy — no gate, a diagnostic warning

Given F2 (the API cannot report failure) and F4 (the obvious detector is unsound), the design does **not**
attempt to gate on image content.

- Return whatever `PrintWindow` produced.
- Emit a **diagnostic** when the full window bitmap is effectively a single colour.
- **Never refuse** on that signal.

**The diagnostic is an ALWAYS-PRESENT metadata field, not a conditional message.** There is no warning
channel in any tool response today — `ToolResponse.Image(png, metadata)` takes an anonymous object of
named fields — so this follows the AB-9 precedent already in this same method:

> ⚠ AB-9: ALWAYS present, empty when nothing was skipped … a diagnostic that appears only on failure
> teaches consumers to ignore its absence.

So a boolean that is present on every capture, `false` in the normal case — not a sentence that shows up
only when something is wrong. agy proposed appending a warning to the text response; the repo's own
convention is better and is followed instead.

⚠ **The exact predicate is the PLAN's to settle, by measurement.** "Effectively a single colour" is not
implementable as written. The plan chooses the predicate and its sampling strategy, and proves it against
both a known-blank (flag 0) and a known-good dark render (see the unverified-signal note in the decision
record).

**Why not refuse.** The instinct comes from SP4's `RedactionUnmaskable`, which refuses rather than
returning an all-black image (`PerceptionManager.cs:894`). That precedent does **not** transfer, and the
code says why at `:902-905`:

> So a raw COMException escaping here does not fail the capture — it silently drops this window's ENTIRE
> mask set and photographs it in the clear. **That is a leak.**

That refusal protects a **privacy guarantee**. A blank `PrintWindow` render exposes nothing. The hazard
that mandated refusal is absent, so importing its posture would buy nothing and cost a false-refusal mode.

**The asymmetry that decides it.** A blank image is *recoverable* — the agent can fall back to
`desktop_snapshot` for the UIA tree. A false refusal is a *hard block* on capturing that window at all.
An incorrect warning is cheap; an incorrect refusal is not.

⚠ **The detector runs on the FULL window bitmap, BEFORE any element crop.** A legitimate crop of a solid
panel — a spacer, a blank text area, a flat background — is genuinely one colour. Evaluating the signal
post-crop would warn constantly on valid captures.

⚠ **Known false-positive, accepted:** a window that is genuinely one uniform colour (a colour-calibration
app, a black loading screen) will be warned about incorrectly. Accepted precisely because the consequence
is a spurious sentence, not a blocked capture.

### 4. Contract changes

Two fields join the JSON metadata, both ALWAYS present:

| Field | Meaning |
|---|---|
| `captureMethod` | which backend produced the image. Window/element report the `PrintWindow` path; full-desktop reports the scrape |
| the uniform-canvas diagnostic (§3) | `false` on a normal capture |

`captureMethod` matters because the two scopes now produce images by different mechanisms, and a caller
comparing them needs to know which it holds.

Both are carried on `CaptureResult`, which today is:

```csharp
// ScreenCapture.cs:9 — CURRENT shape; the plan adds the two fields above
public sealed record CaptureResult(byte[] Png, int X, int Y, int W, int H, double ScaleApplied, int Redactions);
```

⚠ **Exact field names, types and the record's parameter order are the PLAN's**, not settled here. What is
settled: both are always present, and neither is conditional on something having gone wrong.

The tool description loses **"Focus the window first (no occlusion handling)"** — it stops being true, and
a stale instruction in a tool description is read by every agent on every call.

## Out of scope

- **Full-desktop scope.** Keeps the scrape.
- **Minimized windows.** Still `ElementNotActionable`. `PrintWindow` on a minimized window is unreliable,
  and nothing in item 8 requires changing it.
- **A scrape fallback when `PrintWindow` disappoints.** Rejected: choosing between the two needs the
  unsound detector F4 rules out, and a silent backend switch is worse than a known one.
- **ROADMAP item 13** (bare catches) and any redaction-rule change.

## Testing

- **Headless** cannot exercise `PrintWindow` — it needs a real window. The seam makes this tractable:
  acquisition becomes injectable, so mask/geometry logic stays headless-testable and only the interop is
  Desktop-category.
- **The yardstick correction needs a headless test** proving an off-screen window's masks SURVIVE under
  the `PrintWindow` yardstick and are dropped under the scrape yardstick. This is the leak-shaped case and
  must not be Desktop-only.
- **Desktop-category:** capture an occluded window and assert it is not the occluder's pixels. The probe
  shows this is stageable — a second window over the target, then compare against a known control.
- **Every new gate needs a logic mutant** that turns that specific test red. Repo rule, no exceptions.

## Risks, and what the plan must verify before relying on them

1. **App classes beyond the five measured.** The probe covered DirectX, Win32, XAML/UWP, WPF and the
   shell. Electron/Chromium was NOT measured — VS Code was not running. **The plan must measure at least
   one Chromium-family window before this ships**, since it is a common agent target.
2. **`Capture.Rectangle` also handles the DC lifecycle** that the new path must own: `CreateCompatibleDC`,
   `CreateCompatibleBitmap`, `SelectObject`, and their release. A leak here runs inside a long-lived
   server. The plan owns the exact ownership pattern.
3. **The uniform-colour detector's sampling** — full-bitmap versus a grid — is a cost/accuracy tradeoff
   the plan settles, with a measurement, not a guess.
4. **`ScreenCapture.CaptureRectangle` has one other caller** (full-desktop, `ScreenshotTools.cs:49`). The
   plan must confirm the signature change does not alter its behaviour.

## Decision record

**AGY-FIRST consulted at the approaches fork.** agy first recommended refusing on a unique-ARGB-count
signal (Option C). The operator invoked AGY-NEGOTIATE; the peer was asked to read
`PerceptionManager.cs:865-905` and report what the refusal there protects. It concluded the precedent was
leak-specific, withdrew Option C, and proposed the synthesis adopted above — demote the detector from a
gate to a warning, because "an incorrect warning is cheap; an incorrect refusal is fatal".

⚠ **One of its supporting claims was REFUTED by measurement and must not be re-imported.** It argued a
blank capture is cheap because `Encode` "will still overlay the red UIA bounding boxes… a structurally
perfect, clickable wireframe". No such overlay exists: `ScreenCapture.cs` contains exactly one drawing
operation, `g.FillRectangle(black, rel)` — solid black redaction rects, no `DrawRectangle`, no
`DrawString`, no numbering. The conclusion survives on the recoverable-versus-blocked asymmetry instead,
and the refutation strengthens the warning's value: with no free wireframe, telling the agent to fall back
to the tree is guidance it would otherwise lack.

⚠ **agy's unique-colour signal was never verified** — the probe that would have measured it was stopped.
If the plan wants to use unique-colour count as the warning trigger, **it must measure the claim first**:
that a failed render yields exactly one unique ARGB value while a dark-themed success yields hundreds.
