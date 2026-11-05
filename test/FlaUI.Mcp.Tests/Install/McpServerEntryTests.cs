// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class McpServerEntryTests
{
    [Fact]
    public void ToJsonNode_emits_command_and_empty_args()
    {
        var node = McpServerEntry.ForExe(@"C:\tools\flaui-mcp.exe").ToJsonNode();
        Assert.Equal(@"C:\tools\flaui-mcp.exe", (string)node["command"]!);
        Assert.Empty(node["args"]!.AsArray());
    }
}
