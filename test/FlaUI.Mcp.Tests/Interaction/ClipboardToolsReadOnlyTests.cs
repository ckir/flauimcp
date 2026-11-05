// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Text.Json;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class ClipboardToolsReadOnlyTests
{
    [Fact]
    public async Task Clipboard_set_is_blocked_in_read_only_mode()
    {
        var tools = new ClipboardTools(new ServerOptions(ReadOnly: true, AllowElevation: false));
        var json = await tools.DesktopClipboardSet("anything");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("WriteBlockedReadOnly", doc.RootElement.GetProperty("error").GetString());
    }
}
