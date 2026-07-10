using FlaUI.Mcp.Core.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

// Headless: pure restore-identity DECISION (spec §5.2.9), no UIA (Category!=Desktop).
public class RestoreTargetTests
{
    [Fact]
    public void Unique_title_found_exactly_once_restores_by_title_high_confidence()
    {
        var r = RestoreTarget.Resolve(recordedTitle: "PowerShell", recordedOrdinal: 0,
            wasTitleUnique: true, freshTitles: new[] { "cmd.exe", "PowerShell" });
        Assert.Equal(1, r.SelectIndex);
        Assert.True(r.Restored);
        Assert.Equal("high", r.Confidence);
    }

    [Fact]
    public void Ambiguous_title_falls_back_to_ordinal_reduced_confidence()
    {
        var r = RestoreTarget.Resolve("cmd.exe ", 2, wasTitleUnique: false,
            freshTitles: new[] { "cmd.exe ", "PowerShell", "cmd.exe " });
        Assert.Equal(2, r.SelectIndex);
        Assert.True(r.Restored);
        Assert.Equal("reduced", r.Confidence);
    }

    [Fact]
    public void Unique_title_no_longer_present_falls_back_to_ordinal()
    {
        var r = RestoreTarget.Resolve("PowerShell", 1, wasTitleUnique: true,
            freshTitles: new[] { "cmd.exe", "bash" }); // title gone, ordinal 1 in range
        Assert.Equal(1, r.SelectIndex);
        Assert.True(r.Restored);
        Assert.Equal("reduced", r.Confidence);
    }

    [Fact]
    public void Ordinal_out_of_range_and_title_absent_cannot_restore()
    {
        var r = RestoreTarget.Resolve("PowerShell", 5, wasTitleUnique: true,
            freshTitles: new[] { "cmd.exe" }); // title gone, ordinal 5 out of range
        Assert.Null(r.SelectIndex);
        Assert.False(r.Restored);
        Assert.Equal("none", r.Confidence);
    }

    [Fact]
    public void Unique_title_now_duplicated_is_no_longer_a_confident_match_uses_ordinal()
    {
        var r = RestoreTarget.Resolve("cmd.exe ", 0, wasTitleUnique: true,
            freshTitles: new[] { "cmd.exe ", "cmd.exe " }); // was unique, now 2 matches
        Assert.Equal(0, r.SelectIndex);
        Assert.True(r.Restored);
        Assert.Equal("reduced", r.Confidence);
    }
}
