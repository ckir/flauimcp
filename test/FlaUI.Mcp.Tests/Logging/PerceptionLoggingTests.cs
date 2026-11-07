// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Tests.Perception;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class PerceptionLoggingTests
{
    private static ElementDescriptor Desc(string aid, string name) =>
        new(new[] { 1, 2 }, ControlType.Button, aid, name, null, Array.Empty<int>());

    [Fact]
    public void Lookup_of_a_registered_ref_emits_ref_resolve_found()
    {
        var log = new CapturingLogger<RefRegistry>();
        var refs = new RefRegistry(log);
        var r = refs.Register("w1", Desc("btnOk", "OK"), cached: null);
        refs.Lookup("w1", r);
        var ev = log.Inner.Single("ref.resolve");
        Assert.Equal(r, ev.Fields.Single(f => f.Key == "ref").Value);
        Assert.True((bool)ev.Fields.Single(f => f.Key == "found").Value!);
        Assert.Equal("Button", ev.Fields.Single(f => f.Key == "controlType").Value);
        Assert.True(ev.Fields.Single(f => f.Key == "name").UiText);
        Assert.True(ev.Fields.Single(f => f.Key == "automationId").UiText);
    }

    [Fact]
    public void Lookup_of_an_unknown_ref_emits_found_false_then_throws()
    {
        var log = new CapturingLogger<RefRegistry>();
        var ex = Assert.Throws<ToolException>(() => new RefRegistry(log).Lookup("w1", "e99"));
        Assert.Equal(ToolErrorCode.RefNotFound, ex.Code);
        Assert.False((bool)log.Inner.Single("ref.resolve").Fields.Single(f => f.Key == "found").Value!);
    }

    [Fact]
    public void A_registry_without_a_logger_emits_nothing_and_still_works()
    {
        var refs = new RefRegistry();
        refs.Lookup("w1", refs.Register("w1", Desc("a", "b"), cached: null));
    }

    [Fact]
    public async Task A_failing_wait_emits_wait_end_with_its_code()
    {
        var log = new CapturingLogger<WaitCoordinator>();
        using var d = new AutomationDispatcher();
        var pm = new PerceptionManager(new WindowManager(d), new RefRegistry(), new SnapshotCache());
        var wc = new WaitCoordinator(pm, log);
        // An unknown handle either throws (code set) or returns unsatisfied (code null); both must log.
        string? thrown = null;
        try { await wc.WaitForAsync(new WindowHandle("w999"), "name", "Save", "exists", null, timeoutMs: 100, pollIntervalMs: 50); }
        catch (ToolException ex) { thrown = ex.Code.ToString(); }
        var ev = log.Inner.Single("wait.end");
        Assert.Equal("wait_for", ev.Fields.Single(f => f.Key == "kind").Value);
        Assert.False((bool)ev.Fields.Single(f => f.Key == "satisfied").Value!);
        Assert.Equal(thrown, ev.Fields.Single(f => f.Key == "code").Value);
        Assert.True(ev.Fields.Single(f => f.Key == "value").UiText);
    }

    [Fact]
    public async Task A_refused_capture_emits_capture_decision_with_its_code()
    {
        var log = new CapturingLogger<WindowCaptureCoordinator>();
        var wcc = new WindowCaptureCoordinator(
            (_, _) => throw new ToolException(ToolErrorCode.TargetDenied, "denied"),
            new FakeWindowImageSource(_ => throw new InvalidOperationException("not reached")),
            CaptureRetryOptions.Default, logger: log);
        await Assert.ThrowsAsync<ToolException>(() => wcc.CaptureAsync(new WindowHandle("w1"), null, CaptureScope.Window, 1600));
        var ev = log.Inner.Single("capture.decision");
        Assert.False((bool)ev.Fields.Single(f => f.Key == "ok").Value!);
        Assert.Equal("TargetDenied", ev.Fields.Single(f => f.Key == "code").Value);
        Assert.Equal("Window", ev.Fields.Single(f => f.Key == "scope").Value);
    }

    [Fact]
    public void Selector_resolve_event_shape()
    {
        var log = new CapturingLogger<PerceptionManager>();
        using var d = new AutomationDispatcher();
        var pm = new PerceptionManager(new WindowManager(d), new RefRegistry(), new SnapshotCache(), logger: log);
        pm.EmitSelectorResolve(new Selector(AutomationId: "btnOk", Name: "OK", ControlType: "Button"), candidates: 2, chosen: null);
        var ev = log.Inner.Single("selector.resolve");
        Assert.Equal("selector", ev.Fields.Single(f => f.Key == "path").Value);
        Assert.Equal(2, ev.Fields.Single(f => f.Key == "candidates").Value);
        Assert.True(ev.Fields.Single(f => f.Key == "name").UiText);
        Assert.False(ev.Fields.Single(f => f.Key == "controlType").UiText);
    }
}