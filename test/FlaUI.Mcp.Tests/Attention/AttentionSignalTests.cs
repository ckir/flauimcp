using System.Collections.Generic;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class AttentionSignalTests
{
    private sealed class Rec : IAttentionSignal
    {
        public readonly List<string> Fired = new();
        public bool Enabled { get; init; } = true;
        public void Signal(WindowHandle t) => Fired.Add(t.Id);
    }

    [Fact]
    public void Null_signal_is_disabled_and_no_ops()
    {
        var n = NullAttentionSignal.Instance;
        Assert.False(n.Enabled);
        n.Signal(new WindowHandle("w1")); // must not throw
    }

    [Fact]
    public void Composite_fans_out_to_every_enabled_child()
    {
        var a = new Rec(); var b = new Rec();
        var c = new CompositeAttentionSignal(new IAttentionSignal[] { a, b });
        Assert.True(c.Enabled);
        c.Signal(new WindowHandle("w7"));
        Assert.Equal(new[] { "w7" }, a.Fired);
        Assert.Equal(new[] { "w7" }, b.Fired);
    }

    [Fact]
    public void Composite_never_throws_when_a_child_throws()
    {
        var boom = new ThrowingSignal(); var ok = new Rec();
        var c = new CompositeAttentionSignal(new IAttentionSignal[] { boom, ok });
        c.Signal(new WindowHandle("w1"));  // boom throws internally; ok must still fire
        Assert.Equal(new[] { "w1" }, ok.Fired);
    }

    private sealed class ThrowingSignal : IAttentionSignal
    { public bool Enabled => true; public void Signal(WindowHandle t) => throw new System.Exception("boom"); }

    [Fact]
    public void Composite_disabled_when_no_enabled_children()
    {
        var c = new CompositeAttentionSignal(new IAttentionSignal[] { new Rec { Enabled = false } });
        Assert.False(c.Enabled);
    }
}
