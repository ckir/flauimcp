using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class RecordingSyntheticInputTests
{
    [Fact]
    public void Records_when_the_expected_root_still_matches()
    {
        var env = new FakePlatformEnvironment { ForegroundRoot = (nint)1 };
        var rec = new RecordingSyntheticInput(env);
        rec.KeyType("hi", (nint)1);
        rec.KeyChord(new[] { "Ctrl" }, "S", (nint)1);
        Assert.Equal(new[] { "KeyType:hi", "KeyChord:Ctrl+S" }, rec.Calls);
    }

    [Fact]
    public void Records_the_inter_key_delay_passed_to_KeyType()
    {
        var env = new FakePlatformEnvironment { ForegroundRoot = (nint)1 };
        var rec = new RecordingSyntheticInput(env);
        rec.KeyType("hi", (nint)1, 15);
        Assert.Equal(new[] { "KeyType:hi" }, rec.Calls);
        Assert.Equal(new[] { 15 }, rec.TypeDelays);
    }

    [Fact]
    public void Aborts_without_recording_when_foreground_changed()
    {
        var env = new FakePlatformEnvironment { ForegroundRoot = (nint)2 };   // focus stolen
        var rec = new RecordingSyntheticInput(env);
        var ex = Assert.Throws<ToolException>(() => rec.KeyType("secret", (nint)1));
        Assert.Equal(ToolErrorCode.ElementDisappearedDuringAction, ex.Code);
        Assert.Empty(rec.Calls);
    }

    [Fact]
    public void Mouse_verbs_reverify_against_the_hit_test_root()
    {
        var env = new FakePlatformEnvironment { PointResult = new((nint)5, "notepad", "Notepad") };
        var rec = new RecordingSyntheticInput(env);
        rec.MouseClick(10, 20, "left", 1, System.Array.Empty<string>(), (nint)5);   // matches → records
        Assert.Single(rec.Calls);
        env.PointResult = new((nint)6, "x", "Y");                                   // moved under pointer
        Assert.Throws<ToolException>(() => rec.MouseClick(10, 20, "left", 1, System.Array.Empty<string>(), (nint)5));
    }

    [Fact]
    public void Drag_aborts_without_recording_when_the_START_root_changed()
    {
        // an overlay/focus-steal at the START coord must abort BEFORE any mouse-down fires (merge-gate blocker).
        var env = new FakePlatformEnvironment { PointResult = new((nint)5, "notepad", "Notepad") };
        var rec = new RecordingSyntheticInput(env);
        var ex = Assert.Throws<ToolException>(() => rec.MouseDrag(0, 0, 10, 10, "left", (nint)9, (nint)5));
        Assert.Equal(ToolErrorCode.ElementDisappearedDuringAction, ex.Code);
        Assert.Empty(rec.Calls);
    }
}
