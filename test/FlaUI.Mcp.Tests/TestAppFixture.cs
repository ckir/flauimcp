using System.Diagnostics;

namespace FlaUI.Mcp.Tests;

/// <summary>Launches the built TestApp exe and ensures it is killed after tests.</summary>
public sealed class TestAppFixture : IDisposable
{
    public Process Process { get; }
    public string ExePath { get; }

    public TestAppFixture()
    {
        // test assembly runs from test/FlaUI.Mcp.Tests/bin/Debug/net10.0-windows
        var root = AppContext.BaseDirectory;
        ExePath = Path.GetFullPath(Path.Combine(
            root, "..", "..", "..", "..", "..",
            "test", "FlaUI.Mcp.TestApp", "bin", "Debug", "net10.0-windows",
            "FlaUI.Mcp.TestApp.exe"));
        if (!File.Exists(ExePath))
            throw new FileNotFoundException(
                $"TestApp not built at {ExePath}. Run: dotnet build test/FlaUI.Mcp.TestApp");

        Process = Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true })!;
        Process.WaitForInputIdle(5000);
        Thread.Sleep(500); // let the window realize
    }

    public void Dispose()
    {
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }
}
