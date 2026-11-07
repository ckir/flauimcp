// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>Logger + error pointer for the static ToolResponse helpers (spec §4.5). Configured once by
/// Program.cs; tests override per async flow so parallel tests never share the static.</summary>
public static class ToolLog
{
    private static ILogger? s_logger;
    private static Func<string?>? s_pointer;
    private static readonly AsyncLocal<Holder?> s_override = new();
    private sealed record Holder(ILogger? Logger, Func<string?>? Pointer);

    public static void Configure(ILogger? logger, Func<string?>? pointer) { s_logger = logger; s_pointer = pointer; }

    public static IDisposable Override(ILogger? logger, Func<string?>? pointer)
    {
        var previous = s_override.Value;
        s_override.Value = new Holder(logger, pointer);
        return new Restore(previous);
    }

    public static ILogger? Logger => s_override.Value is { } o ? o.Logger : s_logger;

    /// <summary>`"<cid> in <path>"`, or null when logging is off or no call is in scope.</summary>
    public static string? Pointer()
    {
        try { return (s_override.Value is { } o ? o.Pointer : s_pointer)?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _ = ex; return null; }
    }

    private sealed class Restore(Holder? previous) : IDisposable
    { public void Dispose() => s_override.Value = previous; }
}
