// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Tests.Tray;

/// <summary>Launches test/FlaUI.Mcp.TrayFixture with a unique tooltip and stops it GRACEFULLY (registered
/// exit message → NotifyIcon removed). Kill is the last resort only: a killed fixture leaves a ghost icon
/// that a later test could match (SPEC §7, F11).</summary>
public sealed class TrayFixtureHost : IDisposable
{
    public Process Process { get; }
    public string Tooltip { get; }
    public string Kind { get; }
    public string LogPath => Path.Combine(Path.GetTempPath(), $"flaui-trayfixture-{Process.Id}.log");

    public TrayFixtureHost(string kind, string? tooltipPrefix = null)
    {
        Kind = kind;
        Tooltip = $"{tooltipPrefix ?? "FlaUI Tray Fixture"} {Guid.NewGuid():N}";
        var root = AppContext.BaseDirectory;
        string Candidate(string config) => Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", "..",
            "test", "FlaUI.Mcp.TrayFixture", "bin", config, "net10.0-windows", "FlaUI.Mcp.TrayFixture.exe"));
        var thisConfig = new DirectoryInfo(root.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Debug";
        var exe = new[] { thisConfig, "Release", "Debug" }.Select(Candidate).FirstOrDefault(File.Exists)
                  ?? throw new FileNotFoundException($"TrayFixture not built. Run: dotnet build -c {thisConfig} test/FlaUI.Mcp.TrayFixture");
        Process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = $"--kind {kind} --tooltip \"{Tooltip}\"" })!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !Events().Any(e => e.StartsWith("started ")))
        {
            if (Process.HasExited) throw new InvalidOperationException($"TrayFixture exited early (code {Process.ExitCode})");
            Thread.Sleep(50);
        }
        if (!Events().Any(e => e.StartsWith("started "))) throw new TimeoutException("TrayFixture did not log 'started' within 10 s");
        Thread.Sleep(500); // let explorer place the icon
    }

    public string[] Lines()
    {
        try { return File.Exists(LogPath) ? File.ReadAllLines(LogPath) : Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    /// <summary>Lines after the 12-char timestamp + space, e.g. "command Alpha", "left-click", "menu opened".</summary>
    public string[] Events() => Lines().Select(l => l.Length > 13 ? l[13..] : l).ToArray();

    public bool WaitForEvent(string text, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline) { if (Events().Contains(text)) return true; Thread.Sleep(50); }
        return false;
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                var hwnd = FindWindowW(null, $"FlaUI.Mcp.TrayFixture {Process.Id}");
                if (hwnd != IntPtr.Zero) PostMessageW(hwnd, RegisterWindowMessageW("FlaUI.Mcp.TrayFixture.Exit"), IntPtr.Zero, IntPtr.Zero);
                if (!Process.WaitForExit(3000)) { Process.Kill(entireProcessTree: true); Process.WaitForExit(3000); }
            }
        }
        catch { /* already gone */ }
        try { File.Delete(LogPath); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}
