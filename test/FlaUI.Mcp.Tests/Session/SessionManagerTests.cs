using FlaUI.Mcp.Core.Session;
using Xunit;

namespace FlaUI.Mcp.Tests.Session;

public class SessionManagerTests
{
    [Fact]
    public void Terminate_with_kill_policy_on_kills_owned_processes_and_frees_handles()
    {
        var killed = new List<int>();
        var freed = new List<string>();
        var session = new SessionManager(
            killSpawnedOnDisconnect: true,
            killProcess: killed.Add,
            freeHandle: freed.Add);

        session.TrackHandle("w1");
        session.TrackOwnedProcess("w1", pid: 1234);

        session.Terminate();

        Assert.Contains("w1", freed);
        Assert.Contains(1234, killed);
    }

    [Fact]
    public void Terminate_with_kill_policy_off_frees_handles_but_keeps_processes()
    {
        var killed = new List<int>();
        var freed = new List<string>();
        var session = new SessionManager(
            killSpawnedOnDisconnect: false,
            killProcess: killed.Add,
            freeHandle: freed.Add);

        session.TrackHandle("w1");
        session.TrackOwnedProcess("w1", pid: 1234);

        session.Terminate();

        Assert.Contains("w1", freed);
        Assert.Empty(killed);
    }
}
