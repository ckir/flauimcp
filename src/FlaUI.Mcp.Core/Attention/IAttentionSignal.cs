using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Attention;

/// <summary>The attention-signal seam (spec §4.2). Signal is best-effort and MUST NEVER throw — a failed
/// signal must not turn a tool result into an error. Mirrors IActionOverlay's Null/real DI split.</summary>
public interface IAttentionSignal
{
    bool Enabled { get; }
    void Signal(WindowHandle target);
}

/// <summary>Default binding when no channels are active: nothing.</summary>
public sealed class NullAttentionSignal : IAttentionSignal
{
    public static readonly NullAttentionSignal Instance = new();
    private NullAttentionSignal() { }
    public bool Enabled => false;
    public void Signal(WindowHandle target) { }
}

/// <summary>Fans a Signal out to every child; swallows a child fault so one bad channel never breaks the
/// others or the tool. Enabled iff any child is enabled.</summary>
public sealed class CompositeAttentionSignal : IAttentionSignal
{
    private readonly IReadOnlyList<IAttentionSignal> _children;
    public CompositeAttentionSignal(IReadOnlyList<IAttentionSignal> children) => _children = children;
    public bool Enabled => _children.Any(c => c.Enabled);
    public void Signal(WindowHandle target)
    {
        foreach (var c in _children)
            try { if (c.Enabled) c.Signal(target); } catch { /* best-effort: never throw from the signal path */ }
    }
}
