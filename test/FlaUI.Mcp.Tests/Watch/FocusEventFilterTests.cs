// test/FlaUI.Mcp.Tests/Watch/FocusEventFilterTests.cs
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class FocusEventFilterTests
{
    [Theory]
    [InlineData(WatchEventKind.FocusChanged, 100, 100, true)]   // same process -> deliver
    [InlineData(WatchEventKind.FocusChanged, 100, 200, false)]  // foreign process -> drop (§7)
    [InlineData(WatchEventKind.WindowOpened, 100, 100, true)]
    [InlineData(WatchEventKind.WindowOpened, 100, 200, false)]
    [InlineData(WatchEventKind.FocusChanged, 100, 0, false)]    // unreadable PID (§16.4) -> drop
    [InlineData(WatchEventKind.FocusChanged, 0, 100, false)]    // unreadable sub PID -> drop
    [InlineData(WatchEventKind.StructureChanged, 100, 200, true)] // scope-registered -> always passes
    public void ShouldDeliver_enforces_pid_filter(WatchEventKind kind, int subPid, int srcPid, bool expected)
        => Assert.Equal(expected, FocusEventFilter.ShouldDeliver(kind, subPid, srcPid));
}
