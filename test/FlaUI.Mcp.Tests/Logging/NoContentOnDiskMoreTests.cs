// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Spec §6.2/§6.4: the content parameters NoContentOnDiskTests does not reach.</summary>
public sealed class NoContentOnDiskMoreTests
{
    private const string Sentinel = "ZQX-sentinel-9f3a";

    private sealed class NotSensitive : ICallSensitivity
    {
        public (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args) => (false, false);
    }

    [Theory]
    [InlineData("desktop_launch_app", """{"path":"C:\\Windows\\notepad.exe","args":"ZQX-sentinel-9f3a"}""")]
    [InlineData("desktop_wait_for", """{"window":"w1","by":"name","value":"OK","until":"valueEquals","equals":"ZQX-sentinel-9f3a"}""")]
    public async Task The_sentinel_bytes_never_reach_the_log_file(string tool, string json)
    {
        using var h = new OnDiskHarness(_ => false);
        var filter = new ToolCallLogFilter(h.Factory.CreateLogger("t"),
            ToolParameterCatalog.FromAssembly(typeof(ToolResponse).Assembly), new NotSensitive(), h.Hasher);
        await filter.RunAsync(tool, OnDiskHarness.Args(json), () => new ValueTask<CallToolResult>(new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text =
                $$"""{"error":"INTERNAL","message":"rejected {{Sentinel}}","suggestedRecovery":null}""" } },
        }));
        var bytes = h.CloseAndReadAll();
        Assert.NotEmpty(bytes);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Sentinel)));
    }
}
