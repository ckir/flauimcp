using FlaUI.Mcp.Core.Attention;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class ForegroundGateTests
{
    [Fact]
    public void Target_is_foreground_returns_null_result()
    {
        // expected == live foreground → no problem, gate passes (null = "go ahead").
        var r = ForegroundGate.Evaluate(targetRoot: 100, foregroundRoot: 100, "w1",
            resolveProcess: _ => "notepad", ownerHwnd: _ => 0);
        Assert.Null(r);
    }

    [Fact]
    public void Not_foreground_returns_process_name_only_never_title()
    {
        var r = ForegroundGate.Evaluate(targetRoot: 100, foregroundRoot: 200, "w1",
            resolveProcess: h => h == 200 ? "chrome" : "notepad",
            ownerHwnd: _ => 0 /* foreground is NOT an owned modal of target */);
        Assert.NotNull(r);
        Assert.Equal("w1", r!.TargetWindow);
        Assert.Equal("chrome", r.CurrentForeground.Process);
        Assert.Null(r.CurrentForeground.Title);            // leak rule: no title
        Assert.Equal("call-wait-for-foreground", r.RecommendedAction);
        Assert.Contains("desktop_wait_for_foreground", r.Recovery);
    }

    [Fact]
    public void Title_disclosed_only_for_modal_owned_by_exact_target()
    {
        // foreground(200) is a modal whose GW_OWNER == target(100): a title MAY be returned.
        var r = ForegroundGate.Evaluate(targetRoot: 100, foregroundRoot: 200, "w1",
            resolveProcess: _ => "notepad",
            ownerHwnd: h => h == 200 ? 100 : 0,
            resolveTitle: _ => "Save changes?");
        Assert.NotNull(r);
        Assert.Equal("Save changes?", r!.CurrentForeground.Title);
    }

    [Fact]
    public void No_foreground_window_recommends_wait_not_launch()
    {
        // foregroundRoot 0 (nothing foreground) is still "not our target" → wait.
        var r = ForegroundGate.Evaluate(targetRoot: 100, foregroundRoot: 0, "w1",
            resolveProcess: _ => null, ownerHwnd: _ => 0);
        Assert.NotNull(r);
        Assert.Equal("call-wait-for-foreground", r!.RecommendedAction);
        Assert.Null(r.CurrentForeground.Process);
    }
}
