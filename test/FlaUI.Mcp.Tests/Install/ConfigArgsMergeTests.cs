using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ConfigArgsMergeTests
{
    [Fact]
    public void Adds_flag_group_preserving_siblings()
    {
        var merged = ConfigArgsMerge.Apply(new[] { "--overlay", "--overlay-ms=800" },
            add: new[] { "--autosound" }, removeAnyOf: new[] { "--autosound" });
        Assert.Contains("--overlay", merged);
        Assert.Contains("--overlay-ms=800", merged);
        Assert.Contains("--autosound", merged);
    }

    [Fact]
    public void Removes_only_the_named_group()
    {
        var merged = ConfigArgsMerge.Apply(new[] { "--overlay", "--overlay-ms=800", "--autosound" },
            add: System.Array.Empty<string>(), removeAnyOf: new[] { "--autosound" });
        Assert.Contains("--overlay", merged);
        Assert.DoesNotContain("--autosound", merged);
    }

    [Fact]
    public void Is_idempotent_no_duplicate_flags()
    {
        var merged = ConfigArgsMerge.Apply(new[] { "--autosound" },
            add: new[] { "--autosound" }, removeAnyOf: new[] { "--autosound" });
        Assert.Single(System.Array.FindAll(merged, a => a == "--autosound"));
    }

    [Fact]
    public void Group_removal_drops_bare_and_valued_members_by_prefix()
    {
        var merged = ConfigArgsMerge.Apply(new[] { "--overlay", "--overlay-ms=800", "--autosound" },
            add: System.Array.Empty<string>(), removeAnyOf: new[] { "--overlay", "--overlay-ms" });
        Assert.DoesNotContain("--overlay", merged);
        Assert.DoesNotContain("--overlay-ms=800", merged);
        Assert.Contains("--autosound", merged);
    }
}
