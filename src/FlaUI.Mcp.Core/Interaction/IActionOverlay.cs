using System.Threading;
using System.Threading.Tasks;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>A physical-screen rectangle the overlay draws (L,T,W,H px). Degenerate when either dimension
/// is <= 0 (UIA returns collapsed/offscreen elements as zero/negative size) — those must skip the flash
/// (SEAT-J fold: feeding them to SetWindowPos/GDI can cause a full-screen paint or a P/Invoke error).</summary>
public readonly record struct OverlayRect(int L, int T, int W, int H)
{
    public bool IsDegenerate => W <= 0 || H <= 0;
}

/// <summary>The intent-overlay seam. Injected into the mutative action wrappers; the default DI binding is
/// NullActionOverlay unless --overlay is on. `Enabled` gates the (non-free) bounds-resolve the wrappers do
/// before previewing — when false they skip it entirely (INV-OV-1 zero default cost).</summary>
public interface IActionOverlay
{
    bool Enabled { get; }
    /// <summary>Show a red rect at `rect` for the configured duration, then hide it — awaited OFF the
    /// caller's action STA (the caller awaits this on its async continuation). MUST swallow all failures
    /// (INV-OV-4) and no-op on a degenerate rect.</summary>
    Task PreviewAsync(OverlayRect rect);
}

/// <summary>Default binding when the overlay is off: literally nothing. Zero threads, zero windows, zero latency.</summary>
public sealed class NullActionOverlay : IActionOverlay
{
    public static readonly NullActionOverlay Instance = new();
    private NullActionOverlay() { }
    public bool Enabled => false;
    public Task PreviewAsync(OverlayRect rect) => Task.CompletedTask;
}

/// <summary>Monotonic request token for the single shared overlay window (INV-OV-6). Each Show mints the
/// newest token; a Hide applies only if it still owns the current token, so a stale hide from an
/// earlier-finishing concurrent action never clears a later action's rect (last-writer-wins).</summary>
public sealed class OverlayTokenGate
{
    private long _current;
    public long Next() => Interlocked.Increment(ref _current);
    public bool OwnsCurrent(long token) => Interlocked.Read(ref _current) == token;
}

/// <summary>The overlay window's sentinel window-class. The perception layer filters any window with this
/// class out of every surface (spec §5.4) so the overlay can never appear in a snapshot / be found / be
/// targeted. Lives in Core so both the GDI impl (Server) and the perception filters (Core) share ONE constant.</summary>
public static class OverlaySentinel
{
    public const string ClassName = "FlaUiMcpIntentOverlay";
}
