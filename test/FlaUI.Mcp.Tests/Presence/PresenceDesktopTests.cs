using FlaUI.Mcp.Core.Presence;
using Xunit;

namespace FlaUI.Mcp.Tests.Presence;

[Trait("Category", "Desktop")]   // match the repo's existing Desktop trait convention
public class PresenceDesktopTests
{
    [Fact]
    public void Real_idle_source_reports_active_right_after_input()
    {
        var idle = new Win32IdleSource().IdleMs();
        Assert.True(idle >= 0);
        // Run this immediately after touching the console → recent input → active bucket.
        Assert.Equal(Activity.Active, IdleActivity.Bucket(idle, 60_000, 300_000));
    }
}
