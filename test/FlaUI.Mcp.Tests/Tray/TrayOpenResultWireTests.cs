// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Tray;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

/// <summary>SPEC 1.1 + F8: fields not listed for a case are ABSENT, never null/false.</summary>
public class TrayOpenResultWireTests
{
    [Fact]
    public void Without_overflow_only_handle_is_emitted()
        => Assert.Equal("{\"handle\":\"w1\"}", ToolResponse.Ok(new TrayOpenResult("w1", null, false)));

    [Fact]
    public void With_overflow_left_open_both_fields_are_emitted()
    {
        var json = ToolResponse.Ok(new TrayOpenResult("w1", "w2", true));
        Assert.Contains("\"overflowHandle\":\"w2\"", json);
        Assert.Contains("\"overflowLeftOpen\":true", json);
    }
}
