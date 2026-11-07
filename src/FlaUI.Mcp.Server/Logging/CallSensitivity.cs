// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Server.Logging;

public interface ICallSensitivity
{
    (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args);
}

/// <summary>Spec §6.3. Pure Win32 + in-memory lookups only: never queues behind UIA work.</summary>
public sealed class CallSensitivity : ICallSensitivity
{
    private readonly WindowManager _windows;
    private readonly SensitivityClassifier _classifier;
    private readonly bool _rulesLoaded;

    public CallSensitivity(WindowManager windows, SensitivityClassifier classifier, ServerOptions options)
    { _windows = windows; _classifier = classifier; _rulesLoaded = options.RedactionRules is not null; }

    /// <summary>The window-level predicate: denylisted (null/empty process counts as denied,
    /// PerceptionPolicy.cs:54-55) or a process an operator rule could match.</summary>
    public bool IsSensitiveProcess(string? process) =>
        PerceptionPolicy.IsDenied(process) || _classifier.CouldMatchProcess(process);

    public (bool Sensitive, bool Targetless) Decide(ToolEntry? tool, IDictionary<string, JsonElement>? args)
    {
        if (args is not null && args.TryGetValue("window", out var w) && w.ValueKind == JsonValueKind.String
            && w.GetString() is { Length: > 0 } id)
            return (IsSensitiveProcess(_windows.TryGetAppName(new WindowHandle(id))), false);
        if (tool?.ForegroundTarget == true)
            return (IsSensitiveProcess(ForegroundProcess()), false);
        return (_rulesLoaded, true);
    }

    private static string? ForegroundProcess()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0) return null;
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
