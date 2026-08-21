using System;
using System.Collections.Generic;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Capture;
using Xunit;

namespace FlaUI.Mcp.Tests.Capture;

public class CaptureAuditSignalTests
{
    private sealed class Recorder : IAttentionSignal
    {
        public List<WindowHandle> Signalled { get; } = new();
        public bool Enabled => true;
        public void Signal(WindowHandle target) => Signalled.Add(target);
    }

    private static CaptureAuditSignal Make(Recorder r, bool enabled, bool targetIsForeground)
        => new(r, enabled, foregroundProbe: () => targetIsForeground ? new IntPtr(0x1234) : new IntPtr(0x9999));

    [Fact]
    public void It_fires_for_a_non_foreground_printWindow_capture_when_enabled()
    {
        var r = new Recorder();
        Make(r, enabled: true, targetIsForeground: false)
            .SignalIfOcclusionBypassed(new WindowHandle("w1"), new IntPtr(0x1234), "printWindow");
        Assert.Single(r.Signalled);
    }

    // OFF BY DEFAULT. The trigger over-signals, so an operator opts in.
    [Fact]
    public void It_does_not_fire_when_the_flag_is_off()
    {
        var r = new Recorder();
        Make(r, enabled: false, targetIsForeground: false)
            .SignalIfOcclusionBypassed(new WindowHandle("w1"), new IntPtr(0x1234), "printWindow");
        Assert.Empty(r.Signalled);
    }

    // A foreground window is by definition visible to the person at the console -- nothing was bypassed.
    [Fact]
    public void It_does_not_fire_for_the_foreground_window()
    {
        var r = new Recorder();
        Make(r, enabled: true, targetIsForeground: true)
            .SignalIfOcclusionBypassed(new WindowHandle("w1"), new IntPtr(0x1234), "printWindow");
        Assert.Empty(r.Signalled);
    }

    // A scrape read only what was already on the screen. There is no widening to audit.
    [Fact]
    public void It_does_not_fire_on_the_scrape_path()
    {
        var r = new Recorder();
        Make(r, enabled: true, targetIsForeground: false)
            .SignalIfOcclusionBypassed(new WindowHandle("w1"), new IntPtr(0x1234), "screenScrape");
        Assert.Empty(r.Signalled);
    }

    // Best-effort: a faulting channel must never turn a successful capture into an error.
    [Fact]
    public void A_throwing_channel_does_not_propagate()
    {
        var throwing = new ThrowingSignal();
        var s = new CaptureAuditSignal(throwing, true, () => new IntPtr(0x9999));
        s.SignalIfOcclusionBypassed(new WindowHandle("w1"), new IntPtr(0x1234), "printWindow");
    }

    private sealed class ThrowingSignal : IAttentionSignal
    {
        public bool Enabled => true;
        public void Signal(WindowHandle target) => throw new InvalidOperationException("boom");
    }
}
