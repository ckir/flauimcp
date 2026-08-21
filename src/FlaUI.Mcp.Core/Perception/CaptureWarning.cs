using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>One condition that may make a returned image unusable. `Code` is a stable identifier a
/// caller branches on; `Recourse` is the sentence telling an agent what to do instead.
///
/// ⚠ Deliberately an OBJECT, not a bare sentence. The analogy this field is built on is
/// unmaskedProcesses -- an always-present list, empty when nothing is wrong -- and that analogy holds
/// only on the axis where the entries are programmatic identifiers. A list of English prose would force
/// a consumer to regex wording that will drift, which is a worse contract than the boolean it replaced.</summary>
public sealed record CaptureWarning(string Code, string Recourse);

/// <summary>The six codes this feature ships, with their recourse text. Centralised so the wire
/// contract has exactly one definition -- §5 settles these strings, not the implementation.</summary>
public static class CaptureWarnings
{
    public const string UniformCanvas = "uniformCanvas";
    public const string DesktopCanvasUniform = "desktopCanvasUniform";
    public const string ElementCanvasUniform = "elementCanvasUniform";
    public const string PopupsNotRendered = "popupsNotRendered";
    public const string ScrapeFallbackTargetUnresponsive = "scrapeFallbackTargetUnresponsive";
    public const string ScrapeFallbackTargetChanging = "scrapeFallbackTargetChanging";

    private static readonly Dictionary<string, string> Recourses = new(StringComparer.Ordinal)
    {
        // ⚠ THE TEXT MUST BE TRUE ON BOTH BACKENDS, and an earlier version said "rendered" -- which is
        // false of a scrape, where nothing rendered and the screen was photographed. The peer argued from
        // that wording that the code should not fire on a scrape at all. Rejected: a uniform scrape of a
        // window's rect is very often a solid OCCLUDER, which is precisely the hazard the fallback
        // carries, and this design's standing rule is that an incorrect warning is cheap while an
        // incorrect refusal is not. The wording was the defect, not the firing.
        // *(AGY-AFTER panel over this plan, round 9, Adversary of the Reviewer.)*
        [UniformCanvas] =
            "The captured region came back as a single colour, so this image may not be usable. Under " +
            "captureMethod 'printWindow' that means the window did not render; under 'screenScrape' it " +
            "may be a solid window covering the target, or a genuinely blank region. Either way, read " +
            "the UIA tree with desktop_snapshot rather than trusting these pixels.",
        [DesktopCanvasUniform] =
            "The whole desktop came back a single colour. The usual causes are a secure desktop (a UAC " +
            "prompt), DRM-protected content, or a session in transition. UIA is normally blocked in " +
            "those states too, so desktop_snapshot will not help - wait for the condition to clear and " +
            "re-capture.",
        [ElementCanvasUniform] =
            "This element's pixels may have failed to render even though the window as a whole did - a " +
            "hardware-accelerated child viewport is the usual cause. Verify through the UIA tree before " +
            "concluding the control is blank.",
        [PopupsNotRendered] =
            "An open menu, dropdown or tooltip belonging to this window is a separate top-level window " +
            "and may be missing from this image - structurally absent under printWindow, and cropped off " +
            "under screenScrape wherever it extends beyond the window's rect. Its absence is not evidence " +
            "it failed to open - read the UIA tree to see it.",
        [ScrapeFallbackTargetUnresponsive] =
            "This image is a screen scrape, so anything overlapping the window is in it - treat occlusion " +
            "as possible. The target is not pumping messages: it will not respond to input either, so do " +
            "not queue clicks against it.",
        [ScrapeFallbackTargetChanging] =
            "This image is a screen scrape, so treat occlusion as possible. The target is changing " +
            "continuously - it is alive and busy, not stuck; waiting and re-capturing may succeed.",
    };

    // ⚠ SIX, not seven. `windowResized` was retired at panel round 11 and this is not an omission.
    // It existed for one path: a window-scope capture that resized with an EMPTY mask set, which
    // continued to the crop and warned. That path was a LEAK -- an empty mask set means "nothing
    // sensitive was found in the T1 layout", not "nothing in this window is sensitive", and a reflow can
    // move or create content the walk never masked. A resize now always retries, so no image is ever
    // returned FOR a window that resized, and a code describing one would never fire.
    // **A documented code that cannot fire is worse than no code**: a consumer writes a handler for it
    // and concludes, from its absence, that no window ever resizes.
    public static IReadOnlyList<string> AllCodes { get; } = new[]
    {
        UniformCanvas, DesktopCanvasUniform, ElementCanvasUniform,
        PopupsNotRendered, ScrapeFallbackTargetUnresponsive, ScrapeFallbackTargetChanging,
    };

    /// <summary>Build the warning for a code. Throws on an unknown code rather than inventing a
    /// recourse: a warning whose instruction is empty is the failure mode §3 exists to prevent.</summary>
    public static CaptureWarning For(string code)
        => Recourses.TryGetValue(code, out var r)
            ? new CaptureWarning(code, r)
            : throw new ArgumentOutOfRangeException(nameof(code), code, "not a shipped capture-warning code");
}
