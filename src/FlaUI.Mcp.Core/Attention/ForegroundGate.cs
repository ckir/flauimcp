using System;

namespace FlaUI.Mcp.Core.Attention;

/// <summary>The leak-safe foreground identity surfaced when a keyboard action can't reach its target.
/// Process name only by default; Title is non-null ONLY for a modal owned by the exact target HWND.</summary>
public readonly record struct ForegroundIdentity(string Handle, string? Process, string? Title);

/// <summary>The enriched, JSON-serializable not-foreground result (spec §4.1). Replaces the generic
/// ElementDisappearedDuringAction for the not-foreground cause on the keyboard path.</summary>
public sealed record TargetNotForeground(
    string TargetWindow,
    ForegroundIdentity CurrentForeground,
    string RecommendedAction,   // "call-wait-for-foreground" | "launch-fresh"
    string Recovery);

/// <summary>Pure not-foreground decision + leak-safe payload builder. No Win32 here — the caller injects
/// the live foreground root, a process-name resolver, an owner-HWND resolver, and (only if an owned modal
/// is detected) a title resolver. Returns null when the target IS the foreground (action may proceed).</summary>
public static class ForegroundGate
{
    public static TargetNotForeground? Evaluate(
        nint targetRoot,
        nint foregroundRoot,
        string targetWindowId,
        Func<nint, string?> resolveProcess,
        Func<nint, nint> ownerHwnd,
        Func<nint, string?>? resolveTitle = null)
    {
        if (targetRoot != 0 && targetRoot == foregroundRoot) return null; // target holds foreground → go

        return new TargetNotForeground(
            targetWindowId, DescribeForeground(foregroundRoot, targetRoot, resolveProcess, ownerHwnd, resolveTitle),
            "call-wait-for-foreground",
            "Call `desktop_wait_for_foreground` on this window — do NOT yield the chat turn to wait for the human to click it.");
    }

    /// <summary>Build the leak-safe foreground identity (spec §4.1 leak rule) — process name only, title
    /// ONLY for a modal owned by the exact target HWND. Shared so EVERY tool that reports currentForeground
    /// (the type/key gate, desktop_focus_window, desktop_wait_for_foreground) produces an IDENTICAL shape
    /// (SEAT-D wire-contract fold). `foregroundRoot`==0 → handle "0", no process/title.</summary>
    public static ForegroundIdentity DescribeForeground(
        nint foregroundRoot, nint targetRoot,
        Func<nint, string?> resolveProcess, Func<nint, nint> ownerHwnd, Func<nint, string?>? resolveTitle = null)
    {
        string? process = foregroundRoot != 0 ? resolveProcess(foregroundRoot) : null;
        string? title = null;
        if (foregroundRoot != 0 && targetRoot != 0 && resolveTitle is not null
            && ownerHwnd(foregroundRoot) == targetRoot)
            title = resolveTitle(foregroundRoot);
        return new ForegroundIdentity(foregroundRoot == 0 ? "0" : "0x" + foregroundRoot.ToString("x"), process, title);
    }
}
