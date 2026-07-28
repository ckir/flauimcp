// test/FlaUI.Mcp.Tests/Perception/FixtureIntegrityTests.cs
using System.Collections.Generic;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>Regression guard for the overflow that Task 5 fixed by restructuring MainWindow.xaml
/// into a three-column Grid: the old single vertical column overflowed the clamped window, and
/// the bottom of the UIA tree was spatially culled. This walks the live UIA tree and fails loudly
/// if any element overflows again, instead of letting it silently vanish behind the cull.</summary>
[Trait("Category", "Desktop")]
public class FixtureIntegrityTests
{
    // The one deliberate exception. SpatialOffscreenButton sits at Canvas.Left="5000" BY DESIGN
    // (see MainWindow.xaml's own comment on the sentinel), and OffscreenCullTests.cs:45 depends on
    // it landing outside the window so the spatial cull backstop has something real to catch. An
    // unexempted walk would find it at x~5080 against a ~894 DIP window and fail permanently --
    // forbidding the exact overflow it exists to create.
    private const string ExemptAutomationId = "SpatialOffscreenButton";

    [Fact]
    public async Task Every_descendant_is_contained_within_the_window_bounding_rectangle()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(app.Process.Id);

        var violations = await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
        {
            var found = new List<string>();

            // LOAD-BEARING #1: compare against the window's OWN UIA BoundingRectangle -- the exact
            // rectangle SnapshotEngine.cs:45-46 assigns to cullBounds, and that SnapshotEngine.cs:69
            // culls against via IntersectsWith. That rect includes the non-client frame and
            // drop-shadow aura. A tighter "client" rect built from
            // SystemParameters.WindowNonClientFrameThickness produces a false RED: content that
            // overflows into the border fails a stricter guard while the cull's own
            // IntersectsWith check still keeps it. Using the cull's own rectangle means the guard
            // and the mechanism it protects can never disagree.
            //
            // This guard is deliberately STRICTER than the cull: the cull drops an element only
            // when it does not intersect cullBounds at ALL, so an element hanging half outside the
            // window still survives culling. That is exactly how Row1Btn's text survived on a 2px
            // sliver. Full containment catches that before it ever becomes a cull.
            var windowRect = win.BoundingRectangle;

            // LOAD-BEARING #3 setup: locate the ONE exempt element by its AutomationId -- the only
            // handle available, since the Canvas that positions it is absent from the UIA tree
            // entirely (WPF layout panels never override OnCreateAutomationPeer). A WPF Button's
            // string Content also gets its own "Text" automation peer as a CHILD of the Button, so
            // exempting only the AutomationId match would still fail on that child (its Name is
            // "Spatial", it has no AutomationId, and it inherits the button's off-window position).
            // Collect the exempt button's own RuntimeId plus its descendants' RuntimeIds up front,
            // so the walk below can skip the whole (tiny, two-element) subtree by identity -- still
            // keyed on the button's AutomationId as the sole entry point, never on locating "the
            // Canvas" or any other ancestor.
            var exemptIds = new HashSet<string>();
            var exemptRoot = Safe(() => win.FindAllDescendants(cf => cf.ByAutomationId(ExemptAutomationId)).FirstOrDefault());
            if (exemptRoot != null)
            {
                var rid = Safe(() => exemptRoot.Properties.RuntimeId.ValueOrDefault);
                if (rid != null) exemptIds.Add(RidKey(rid));
                foreach (var d in Safe(() => exemptRoot.FindAllDescendants()) ?? System.Array.Empty<AutomationElement>())
                {
                    var drid = Safe(() => d.Properties.RuntimeId.ValueOrDefault);
                    if (drid != null) exemptIds.Add(RidKey(drid));
                }
            }

            // LOAD-BEARING #2: walk EVERY descendant -- not the top-level Grid, not its direct
            // children. A Grid in a clamped window is arranged to the client area regardless of
            // what its content does, so "the Grid is inside the window" is an assertion that can
            // never fail. Direct children have the same problem one level down: DupHost/DupRow are
            // GroupBoxes arranged to their column width regardless of content, so a widened inner
            // Button overflows and culls while the GroupBox rect stays neatly inside. Only a full
            // descendant walk closes both holes.
            AutomationElement[] descendants;
            try
            {
                descendants = win.FindAllDescendants();
            }
            catch
            {
                descendants = System.Array.Empty<AutomationElement>();
            }

            foreach (var el in descendants)
            {
                string aid, name;
                ControlType ct;
                System.Drawing.Rectangle rect;
                int[]? rid;
                try
                {
                    // Defensive reads: an element can vanish mid-walk. SnapshotEngine.cs wraps
                    // every property read in a Safe(...) helper for the same reason -- skip an
                    // element we can't read rather than crash the whole walk.
                    aid = el.AutomationId;
                    name = el.Name;
                    ct = el.ControlType;
                    rect = el.BoundingRectangle;
                    rid = el.Properties.RuntimeId.ValueOrDefault;
                }
                catch
                {
                    continue;
                }

                // LOAD-BEARING #3: the ONLY exemption, and it keys on AutomationId -- not on
                // locating "the Canvas" and skipping its subtree. WPF layout panels (Grid,
                // StackPanel, Canvas, Border) never override OnCreateAutomationPeer, so they are
                // ABSENT from the UIA tree entirely (see MainWindow.xaml's comment on DupHost) and
                // there is no Canvas node to find or skip as a subtree. In the UIA tree
                // SpatialOffscreenButton is simply another descendant of the window, indistinguishable
                // by ancestry from anything else -- AutomationId is the only handle available. For
                // the same reason, scoping the walk per-column StackPanel is inexpressible: those
                // panels are not in the tree, so "every descendant of every column" and "every
                // descendant of the window" denote the same element set. (The exemption also covers
                // the button's own auto-generated text-label child -- see the LOAD-BEARING #3 setup
                // above -- because that child is identified by walking from the AutomationId match,
                // not by any other means.)
                if (rid != null && exemptIds.Contains(RidKey(rid))) continue;

                // Skip zero-area rects: not laid out, cannot overflow. SnapshotEngine.cs:69 treats
                // them the same way.
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                if (rect.Bottom > windowRect.Bottom || rect.Right > windowRect.Right)
                {
                    var label = !string.IsNullOrEmpty(aid) ? aid
                        : !string.IsNullOrEmpty(name) ? $"{name} ({ct})"
                        : $"<{ct}>";
                    found.Add($"{label}: rect={rect} window={windowRect}");
                }
            }

            return found;
        });

        Assert.True(violations.Count == 0,
            $"{violations.Count} descendant(s) overflow the window's UIA BoundingRectangle:\n" +
            string.Join("\n", violations));
    }

    // Same shape as SnapshotEngine's Safe(...) helper: UIA property/search access can throw if an
    // element has vanished by the time we read it. Swallow and treat as "unknown" rather than
    // crashing the whole walk.
    private static T? Safe<T>(System.Func<T?> f) where T : class
    {
        try { return f(); } catch { return default; }
    }

    // Zero-allocation-ish RuntimeId equality key (UIA RuntimeIds are small int[]).
    private static string RidKey(int[] rid) => string.Join(",", rid);
}
