using System;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class UninstallWarningsTests
{
    private static string TempState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Writes_the_warning_lines_where_the_uninstaller_can_find_them()
    {
        var s = TempState();

        var path = UninstallWarnings.Write(s, new[] { "could not re-enable x", "y is gone" });

        Assert.NotNull(path);
        var text = File.ReadAllText(path!);
        Assert.Contains("could not re-enable x", text);
        Assert.Contains("y is gone", text);
    }

    // An empty file would pop an EMPTY dialog at the end of a clean uninstall — worse than silence.
    [Fact]
    public void Writes_no_file_when_there_is_nothing_to_warn_about()
    {
        var s = TempState();

        var path = UninstallWarnings.Write(s, Array.Empty<string>());

        Assert.Null(path);
        Assert.False(File.Exists(UninstallWarnings.PathIn(s)));
    }

    // A clean uninstall after a dirty one must not resurrect the old warnings.
    [Fact]
    public void Writing_nothing_clears_a_stale_warnings_file()
    {
        var s = TempState();
        UninstallWarnings.Write(s, new[] { "old news" });

        UninstallWarnings.Write(s, Array.Empty<string>());

        Assert.False(File.Exists(UninstallWarnings.PathIn(s)), "a stale file would be shown as if it were current");
    }

    [Fact]
    public void A_second_write_replaces_rather_than_appends()
    {
        var s = TempState();
        UninstallWarnings.Write(s, new[] { "first" });

        UninstallWarnings.Write(s, new[] { "second" });

        var text = File.ReadAllText(UninstallWarnings.PathIn(s));
        Assert.DoesNotContain("first", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public void Creates_the_state_dir_when_absent()
    {
        var s = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());   // NOT created

        var path = UninstallWarnings.Write(s, new[] { "something" });

        Assert.NotNull(path);
        Assert.True(File.Exists(path!));
    }

    // The reporter must never itself become the failure.
    [Fact]
    public void An_unwritable_target_returns_null_and_never_throws()
    {
        var s = TempState();
        using (File.Open(UninstallWarnings.PathIn(s), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var path = UninstallWarnings.Write(s, new[] { "blocked" });
            Assert.Null(path);
        }
    }

    [Fact]
    public void The_file_lives_in_the_state_dir_not_the_data_dir()
    {
        // The data dir is what --purge-data destroys; these warnings must outlive that.
        Assert.EndsWith(Path.Combine("state-x", "uninstall-warnings.log"), UninstallWarnings.PathIn("state-x"));
    }
}
