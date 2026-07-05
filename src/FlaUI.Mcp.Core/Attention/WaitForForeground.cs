using System;
using System.Threading;

namespace FlaUI.Mcp.Core.Attention;

/// <summary>Why a wait ended (spec §4.5).</summary>
public enum WaitReason { Gained, Timeout, WindowDestroyed }

/// <summary>The blocking wait outcome; currentForeground filled leak-safe by the tool.</summary>
public readonly record struct WaitResult(bool ForegroundGained, WaitReason Reason);

/// <summary>The event-driven waiter seam. The real impl (Win32ForegroundWaiter) hooks
/// EVENT_SYSTEM_FOREGROUND + EVENT_OBJECT_DESTROY on a dedicated non-STA thread; a fake drives tests.</summary>
public interface IForegroundWaiter
{
    WaitResult Wait(IntPtr targetHwnd, int timeoutMs);
}

public static class WaitForForeground
{
    public const int HardCapMs = 45000;

    /// <summary>Clamp the requested timeout to (0, HardCap]; 0/negative/over-cap → HardCap (spec §4.5).</summary>
    public static int ClampTimeout(int requestedMs)
        => requestedMs > 0 && requestedMs <= HardCapMs ? requestedMs : HardCapMs;

    /// <summary>MaxConcurrentWaiters = 1 (spec §4.5 DoS guard). Non-reentrant single-slot gate.</summary>
    public sealed class WaiterGate
    {
        private int _busy;
        public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
        public void Exit() => Interlocked.Exchange(ref _busy, 0);
    }
}
