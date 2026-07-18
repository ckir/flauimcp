using FlaUI.Mcp.Server.Install;
using Xunit;

public class CliResolverTests
{
    // Fake PATH with two dirs; fileExists says only "C:\\tools\\agy.cmd" exists.
    private static string? ResolveWith(string cli, string path, params string[] existing)
    {
        var set = new HashSet<string>(existing, System.StringComparer.OrdinalIgnoreCase);
        return CliResolver.Resolve(cli, path, pathext: ".COM;.EXE;.BAT;.CMD", fileExists: set.Contains);
    }

    [Fact]
    public void Finds_cmd_shim_via_pathext()
    {
        var found = ResolveWith("agy", @"C:\other;C:\tools", @"C:\tools\agy.cmd");
        Assert.Equal(@"C:\tools\agy.cmd", found);
    }

    [Fact]
    public void Prefers_exe_over_cmd_by_pathext_order()
    {
        var found = ResolveWith("agy", @"C:\tools", @"C:\tools\agy.cmd", @"C:\tools\agy.exe");
        Assert.Equal(@"C:\tools\agy.exe", found); // .EXE precedes .CMD in PATHEXT
    }

    [Fact]
    public void Returns_null_when_absent()
    {
        Assert.Null(ResolveWith("claude", @"C:\tools", @"C:\tools\agy.cmd"));
    }
}
