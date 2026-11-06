// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

[Trait("Category", "Desktop")]
public class TrayDesktopTests
{
    [Fact]
    public void Fixture_starts_logs_and_exits_gracefully()
    {
        string log;
        using (var fx = new TrayFixtureHost("uia"))
        {
            log = fx.LogPath;
            Assert.Contains(fx.Events(), e => e.StartsWith("started kind=uia"));
        }
        Assert.False(File.Exists(log)); // Dispose deleted it after a graceful exit
    }
}
