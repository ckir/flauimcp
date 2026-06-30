using System;
using System.Collections.Generic;
using FlaUI.Mcp.Core.Perception;

namespace FlaUI.Mcp.Core.Interaction;

public enum ActionVerdict { Allowed, Interlocked, Denied }

/// <summary>Classifies a synthetic-input TARGET (owning process base-name + window class). Denied =
/// hard refuse (UAC/secure-desktop/credential stores). Interlocked = refuse unless the lease carries
/// the `shells` capability (terminal / Win+R / browser address bar). Same logic for the ref path and
/// the coordinate hit-test path.</summary>
public static class ActionPolicy
{
    private static readonly HashSet<string> DeniedProcesses =
        new(StringComparer.OrdinalIgnoreCase) { "consent", "winlogon", "lockapp", "logonui", "credentialuihost" };

    private static readonly HashSet<string> InterlockedClasses =
        new(StringComparer.OrdinalIgnoreCase) { "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS" };

    private static readonly HashSet<string> InterlockedProcesses =
        new(StringComparer.OrdinalIgnoreCase) { "windowsterminal", "cmd", "powershell", "pwsh", "conhost" };

    public static ActionVerdict Classify(string? processName, string? windowClass)
    {
        var proc = processName?.Trim();
        var cls = windowClass?.Trim();
        if (!string.IsNullOrEmpty(proc) && (DeniedProcesses.Contains(proc) || PerceptionPolicy.IsDenied(proc)))
            return ActionVerdict.Denied;
        if ((!string.IsNullOrEmpty(cls) && InterlockedClasses.Contains(cls)) ||
            (!string.IsNullOrEmpty(proc) && InterlockedProcesses.Contains(proc)))
            return ActionVerdict.Interlocked;
        return ActionVerdict.Allowed;
    }
}
