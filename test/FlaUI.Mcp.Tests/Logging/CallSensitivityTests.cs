// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Spec §6.3: how a call is classified. The foreground branch depends on the real desktop and is not tested.</summary>
public sealed class CallSensitivityTests : IDisposable
{
    private readonly AutomationDispatcher _d = new();
    private readonly WindowManager _wm;
    public CallSensitivityTests() { _wm = new WindowManager(_d, new CapturingLogger<WindowManager>()); }
    public void Dispose() => _d.Dispose();

    private CallSensitivity Make(bool rulesLoaded) => new(_wm, SensitivityClassifier.OsOnly,
        new ServerOptions(ReadOnly: false, AllowElevation: false, RedactionRules: rulesLoaded ? "rules.json" : null));

    private static ToolEntry Tool(string name) =>
        ToolParameterCatalog.FromAssembly(typeof(ToolResponse).Assembly).Find(name)!;

    private static Dictionary<string, JsonElement> Args(string json) => OnDiskHarness.Args(json);

    [Fact]
    public void A_handle_minted_for_this_process_is_not_sensitive_and_not_targetless()
    {
        var self = Process.GetCurrentProcess().ProcessName;
        Assert.False(PerceptionPolicy.IsDenied(self), $"test host '{self}' is on the deny list");
        var id = _wm.LazyHandleFor((IntPtr)0x7FFF0002, Environment.ProcessId);
        Assert.Equal((false, false), Make(false).Decide(Tool("desktop_get_text"), Args($$"""{"window":"{{id}}"}""")));
    }

    [Fact]
    public void An_unknown_window_id_fails_closed()
    {
        Assert.Equal((true, false), Make(false).Decide(Tool("desktop_get_text"), Args("""{"window":"w999"}""")));
    }

    [Fact]
    public void No_window_arg_is_targetless_and_sensitive_only_when_rules_are_loaded()
    {
        var tool = Tool("desktop_clipboard_set");
        Assert.False(tool.ForegroundTarget);
        var args = Args("""{"text":"x"}""");
        Assert.Equal((false, true), Make(false).Decide(tool, args));
        Assert.Equal((true, true), Make(true).Decide(tool, args));
    }

    [Fact]
    public void An_empty_window_arg_falls_through_to_the_targetless_rule()
    {
        var tool = Tool("desktop_get_text");
        var args = Args("""{"window":""}""");
        Assert.Equal((false, true), Make(false).Decide(tool, args));
        Assert.Equal((true, true), Make(true).Decide(tool, args));
    }
}
