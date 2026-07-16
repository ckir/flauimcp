using System.IO;
using System.Linq;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class CollisionMarkerTests
{
    private static string TempState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DisabledEntry UserEntry = new("flaui-mcp@flaui-mcp", "user", null);
    private static readonly DisabledEntry ProjA = new("flaui-mcp@flaui-mcp", "local", @"C:\a");
    private static readonly DisabledEntry ProjB = new("flaui-mcp@flaui-mcp", "local", @"C:\b");

    [Fact]
    public void Read_on_a_machine_with_no_marker_yields_nothing()
        => Assert.Empty(CollisionMarker.Read(TempState()));

    [Fact]
    public void Recorded_entries_round_trip_with_scope_and_project()
    {
        var s = TempState();

        CollisionMarker.Record(s, new[] { UserEntry, ProjA });

        var read = CollisionMarker.Read(s);
        Assert.Equal(2, read.Count);
        Assert.Contains(UserEntry, read);
        Assert.Contains(ProjA, read);
        Assert.Null(read.Single(e => e.Scope == "user").ProjectPath);
    }

    // R1, per entry: a repair/upgrade re-run must not disturb what an earlier run recorded.
    [Fact]
    public void Recording_an_entry_that_is_already_recorded_changes_nothing()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { ProjA });
        var before = File.ReadAllText(CollisionMarker.PathIn(s));

        CollisionMarker.Record(s, new[] { ProjA });

        Assert.Equal(before, File.ReadAllText(CollisionMarker.PathIn(s)));
        Assert.Single(CollisionMarker.Read(s));
    }

    // The case a FILE-level write-once rule would silently lose: a new entry disabled by a later run.
    [Fact]
    public void Recording_a_new_entry_merges_it_and_keeps_the_existing_ones()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { ProjA });

        CollisionMarker.Record(s, new[] { ProjB });

        var read = CollisionMarker.Read(s);
        Assert.Equal(2, read.Count);
        Assert.Contains(ProjA, read);
        Assert.Contains(ProjB, read);
    }

    // Same id and scope, different project => genuinely different entries.
    [Fact]
    public void Entries_are_distinguished_by_project_not_just_by_id_and_scope()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { ProjA, ProjB });
        Assert.Equal(2, CollisionMarker.Read(s).Count);
    }

    // R7: deleting the marker is part of consuming it. A marker that outlives its uninstall would
    // later re-enable a plugin the USER disabled — the exact outcome R1 exists to prevent.
    [Fact]
    public void Delete_removes_the_marker()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { UserEntry });

        var warning = CollisionMarker.Delete(s);

        Assert.Null(warning);
        Assert.False(File.Exists(CollisionMarker.PathIn(s)));
        Assert.Empty(CollisionMarker.Read(s));
    }

    [Fact]
    public void Delete_with_no_marker_present_is_a_silent_no_op()
        => Assert.Null(CollisionMarker.Delete(TempState()));

    [Fact]
    public void Delete_reports_a_warning_when_the_marker_cannot_be_removed()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { UserEntry });

        using (File.Open(CollisionMarker.PathIn(s), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var warning = CollisionMarker.Delete(s);
            Assert.NotNull(warning);
            Assert.Contains("marker", warning);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("{ \"version\": 1 }")]
    [InlineData("{ \"version\": 1, \"disabled\": \"not an array\" }")]
    public void A_corrupt_marker_reads_as_empty_and_never_throws(string content)
    {
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), content);
        Assert.Empty(CollisionMarker.Read(s));
    }

    // Fail-safe direction: an unreadable marker must mean "restore nothing", never "restore
    // everything" — we must not enable plugins we have no record of disabling.
    [Fact]
    public void Record_creates_the_state_dir_if_it_does_not_exist()
    {
        var s = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());   // NOT created

        CollisionMarker.Record(s, new[] { UserEntry });

        Assert.Single(CollisionMarker.Read(s));
    }

    [Fact]
    public void Recording_an_empty_set_writes_no_marker_at_all()
    {
        var s = TempState();
        CollisionMarker.Record(s, System.Array.Empty<DisabledEntry>());
        Assert.False(File.Exists(CollisionMarker.PathIn(s)), "an empty marker would later fire a no-op restore");
    }

    // ProjectPath is a WINDOWS path: C:\Proj and c:\proj are one directory. Default record equality
    // is case-sensitive, so without SameEntry the same entry would be recorded twice — and, worse,
    // the "did we already record this?" check would MISS, making a re-install wrongly conclude the
    // user disabled it.
    [Fact]
    public void An_entry_differing_only_in_path_casing_is_the_same_entry()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\Proj") });

        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"c:\proj") });

        Assert.Single(CollisionMarker.Read(s));
    }

    [Fact]
    public void Entry_identity_ignores_case_in_id_scope_and_path()
    {
        Assert.True(CollisionMarker.SameEntry(
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\Proj"),
            new DisabledEntry("FlaUI-MCP@FlaUI-MCP", "LOCAL", @"c:\proj")));
        Assert.False(CollisionMarker.SameEntry(
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\a"),
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\b")));
        Assert.False(CollisionMarker.SameEntry(
            new DisabledEntry("flaui-mcp@flaui-mcp", "user", null),
            new DisabledEntry("flaui-mcp@flaui-mcp", "local", null)));
    }

    // We have already disabled the user's plugin by the time we record it. If the record does not
    // survive, uninstall can never put it back — so this failure must be VISIBLE, not swallowed.
    [Fact]
    public void A_marker_that_cannot_be_written_returns_a_warning_rather_than_failing_silently()
    {
        var s = TempState();
        Directory.CreateDirectory(CollisionMarker.PathIn(s));   // a DIRECTORY where the file must go

        var warning = CollisionMarker.Record(s, new[] { UserEntry });

        Assert.NotNull(warning);
        Assert.Contains("will not re-enable it automatically", warning);
    }

    [Fact]
    public void A_successful_record_returns_no_warning()
        => Assert.Null(CollisionMarker.Record(TempState(), new[] { UserEntry }));
}
