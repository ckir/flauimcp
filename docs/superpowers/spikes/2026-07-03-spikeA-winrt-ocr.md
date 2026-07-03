# Spike α — `Windows.Media.Ocr` feasibility on net10.0-windows + coord round-trip

**Date:** 2026-07-03 · **Gates:** Phase 9 Tasks 7–11 (the OCR prong). **Method:** throwaway probe (`_SpikeOcrProbe` in Core + a `[Category=Desktop]` probe test), run once, deleted; findings below.

## VERDICT: **GO**

`Windows.Media.Ocr` works on-box via the built-in CsWinRT projection (no NuGet package). OCR reads clean rendered UI text accurately, and `Word.BoundingRect` is in **top-left bitmap pixels** exactly as the §6 coordinate contract assumes. Task 9 may implement the engine as planned, with one impl-detail deviation (PNG→stream load, below).

## 1. Confirmed TFM / package (Task 1 Step 1)

**Confirmed path:** set `<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>` on **ALL THREE** projects — `FlaUI.Mcp.Core`, `FlaUI.Mcp.Server`, **and** `FlaUI.Mcp.Tests`. **No `<PackageReference>` needed** — the `10.0.19041.0` platform version enables the built-in CsWinRT projections (`Windows.Media.Ocr`, `Windows.Graphics.Imaging.SoftwareBitmap`, `Windows.Storage.Streams`).

- **Plan divergence (fold into Task 9):** the plan's Task 9 file list names only `FlaUI.Mcp.Core.csproj`. That is INSUFFICIENT — bumping only Core yields `error NU1201: Project FlaUI.Mcp.Core is not compatible with net10.0-windows7.0` from the Server and Test projects (bare `net10.0-windows` resolves to platform `7.0`, incompatible with a `10.0.19041` reference). **All three csproj TFMs must move together.**
- **Kept in the spike commit** (this is the confirmed path, so it stays applied — Task 9 does NOT re-apply it; its STATE-VERIFY Step 0 will find the TFM already bumped). Build after the bump: `dotnet build` → **5 projects, 0 errors, 0 warnings**. Existing headless suite after the bump: **315/315 pass** (no regression).

## 2. Confirmed WinRT surface (Task 1 Step 2 → the exact surface Task 9 implements)

- Engine: `OcrEngine.TryCreateFromUserProfileLanguages()` → **non-null on this box** (`RecognizerLanguage.DisplayName` = "English (United States)"). An OCR language pack IS present. If it were null (no pack), the impl must throw `ToolException(OcrUnavailable, …)` — Task 9/10 must never crash. (`OcrEngine.AvailableRecognizerLanguages` / `IsLanguageSupported` exist if a fallback language selection is ever needed; not required for v1.)
- Recognize: `OcrResult result = await engine.RecognizeAsync(softwareBitmap);` then `foreach (var line in result.Lines) foreach (var word in line.Words) { word.Text; word.BoundingRect; }`. `BoundingRect` is a `Windows.Foundation.Rect` with `double` `X/Y/Width/Height`.
- **`BoundingRect` unit/origin = top-left BITMAP pixels — CONFIRMED.** Deterministic probe: GDI-rendered "Submit Order 42" with text top-left at pixel **(30,40)**; OCR returned `'Submit' @ (36,50)`, `'Order' @ (131,50)`, `'42' @ (210,51)` — X increases left→right across the drawn word run, Y≈50 tracks the drawn row (not 0). Matches the §6 assumption; `CoordinateMapping` can invert it directly.
- **PNG → `SoftwareBitmap` decode:** `var decoder = await BitmapDecoder.CreateAsync(inMemoryRandomAccessStream); using var bmp = await decoder.GetSoftwareBitmapAsync();`.
- **DEVIATION (Task 9 should adopt the proven form, NOT the plan's illustrative line):** the plan illustrated loading the PNG bytes with `await stream.WriteAsync(pngBytes.AsBuffer())`. The probe instead loaded them with a `DataWriter` — this is proven to compile+run:
  ```csharp
  using var stream = new InMemoryRandomAccessStream();
  using (var dw = new DataWriter(stream)) { dw.WriteBytes(pngBytes); await dw.StoreAsync(); await dw.FlushAsync(); dw.DetachStream(); }
  stream.Seek(0);
  ```
  This avoids depending on the `AsBuffer()` extension (`System.Runtime.InteropServices.WindowsRuntime`). It is a wire-neutral implementation detail (same `byte[] PNG in → OcrWord[] out` contract), not a shape/type change. Task 9 may use `DataWriter` (recommended, proven) or verify `AsBuffer()` compiles under the confirmed TFM.

## 3. Accuracy + coord round-trip (Task 1 Step 3)

- **Accuracy on clean rendered UI text: excellent.** 28px Segoe UI "Submit Order 42" → read exactly, zero misreads, split correctly into 3 words on one line. The **fuzzy default (§3) still stands** as the right default: real captures add downscale, antialiasing, small fonts, and low-contrast themes that WILL produce misreads/over-splits — the probe's clean sample is a best case, not a guarantee. Do not weaken fuzzy to exact on the strength of this one clean read.
- **Display scale on this box:** the dev box is an **RDP session at 1536×864, 100% DPI → `CaptureResult.ScaleApplied` = 1.0000** (width < the 1920 clamp, so no downscale). The 150%-downscale round-trip could NOT be measured on this hardware. It is covered instead by (a) Task 7 `CoordinateMapping` pure tests, which include an explicit 150% (`scaleApplied ≈ 0.667`) case, and (b) the Task 10 `Category=Desktop` coord-landing test on the real display. The metadata needed for §6 (`CaptureResult.X/Y/ScaleApplied`) is present and correct.
- **Real full-screen capture path:** `ScreenCapture.CaptureRectangle(VirtualScreenBounds, [], maxWidth:0)` → PNG (6.8 KB) → `BitmapDecoder` → `SoftwareBitmap` → `RecognizeAsync` ran **end-to-end with no exception**, returning 0 words (the ambient RDP desktop was near-blank at capture time — a tiny near-uniform PNG). This validates the exact production pipeline path decodes and OCRs a real `ScreenCapture` result without error; real-capture *accuracy* on an app window with known text is validated by the Task 10 Desktop test.

## 4. Failure mode (no language pack)

`TryCreateFromUserProfileLanguages()` returns `null` when the user profile has no OCR language pack. The engine impl (Task 9) guards this and throws `ToolException(ToolErrorCode.OcrUnavailable, "No OCR language pack…", "install a Windows OCR language pack…")` — the tools (Task 10/11) surface it as a clean error, never a crash. (Not reproduced live here — the pack is present — but the guard path is in the planned impl.)

## Bottom line for Task 9

Implement `IOcrEngine`/`WindowsMediaOcrEngine` as planned. Confirmed: TFM `net10.0-windows10.0.19041.0` on **all three** projects (already applied in this spike commit — do not re-apply, do not add a package); surface `OcrEngine.TryCreateFromUserProfileLanguages()` + `RecognizeAsync(SoftwareBitmap)` + `Lines→Words→{Text,BoundingRect(top-left bitmap px)}`; load the PNG via `DataWriter` (not `AsBuffer`); guard the null engine with `OcrUnavailable`.
