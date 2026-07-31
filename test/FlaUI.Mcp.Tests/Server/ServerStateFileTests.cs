using System;
using System.Diagnostics;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

public class ServerStateFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp3state").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void A_written_instance_round_trips_and_reports_live_for_this_process()
    {
        var me = Process.GetCurrentProcess();
        ServerStateFile.Write(_dir, me.Id, me.StartTime.ToUniversalTime(), "C:/rules.json", "abc123");
        var found = Assert.Single(ServerStateFile.ReadAll(_dir));
        Assert.Equal(ServerLiveness.Live, found.Liveness);
        Assert.Equal("abc123", found.RulesSha256);
        Assert.Equal(1, found.StateVersion);
    }

    /// A recycled PID must not read as the same server: the start time cannot match.
    [Fact]
    public void A_recycled_pid_is_detected_by_start_time_and_reports_dead()
    {
        var me = Process.GetCurrentProcess();
        ServerStateFile.Write(_dir, me.Id, me.StartTime.ToUniversalTime().AddDays(-1), "C:/r.json", "x");
        Assert.Equal(ServerLiveness.Dead, Assert.Single(ServerStateFile.ReadAll(_dir)).Liveness);
    }

    [Fact]
    public void A_pid_that_does_not_exist_reports_dead()
    {
        ServerStateFile.Write(_dir, 0x7FFFFFFE, DateTime.UtcNow, "C:/r.json", "x");
        Assert.Equal(ServerLiveness.Dead, Assert.Single(ServerStateFile.ReadAll(_dir)).Liveness);
    }

    [Fact]
    public void An_unrecognised_stateVersion_reports_unknown_not_dead()
    {
        File.WriteAllText(Path.Combine(_dir, "4242.json"),
            """
            { "stateVersion": 99, "pid": 4242, "processStartTimeUtc": "2020-01-01T00:00:00Z",
                 "redactionRulesPath": "C:/r.json", "redactionRulesSha256": "x" }
            """);
        Assert.Equal(ServerLiveness.Unknown, Assert.Single(ServerStateFile.ReadAll(_dir)).Liveness);
    }

    /// ACCEPTANCE CONSTRAINT (b): pruning requires POSITIVE proof of death. A non-elevated CLI that
    /// cannot inspect an elevated server's process must NOT delete that server's state file.
    [Fact]
    public void Prune_removes_only_dead_instances_and_never_unknown_ones()
    {
        var me = Process.GetCurrentProcess();
        ServerStateFile.Write(_dir, 0x7FFFFFFE, DateTime.UtcNow, "C:/r.json", "dead");
        ServerStateFile.Write(_dir, me.Id, me.StartTime.ToUniversalTime(), "C:/r.json", "live");
        File.WriteAllText(Path.Combine(_dir, "4242.json"), """{ "stateVersion": 99, "pid": 4242 }""");

        ServerStateFile.PruneDead(_dir);

        var left = ServerStateFile.ReadAll(_dir);
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, i => i.RulesSha256 == "dead");
        Assert.Contains(left, i => i.Liveness == ServerLiveness.Unknown);
    }
}
