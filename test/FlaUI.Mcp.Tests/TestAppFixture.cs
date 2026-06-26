using System.Diagnostics;

namespace FlaUI.Mcp.Tests;

/// <summary>Launches the built TestApp exe and ensures it is killed after tests.</summary>
public sealed class TestAppFixture : IDisposable
{
    public Process Process { get; }
    public string ExePath { get; }

    public TestAppFixture()
    {
        // The test assembly runs from test/FlaUI.Mcp.Tests/bin/<Config>/net10.0-windows. The TestApp
        // builds to test/FlaUI.Mcp.TestApp/bin/<Config>/net10.0-windows — resolve it config-agnostically
        // (CI builds -c Release; local is Debug) by preferring this assembly's config, then either.
        var root = AppContext.BaseDirectory;
        string Candidate(string config) => Path.GetFullPath(Path.Combine(
            root, "..", "..", "..", "..", "..",
            "test", "FlaUI.Mcp.TestApp", "bin", config, "net10.0-windows",
            "FlaUI.Mcp.TestApp.exe"));
        var thisConfig = new DirectoryInfo(root.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Debug";
        ExePath = new[] { thisConfig, "Release", "Debug" }.Select(Candidate).FirstOrDefault(File.Exists)
                  ?? Candidate(thisConfig);
        if (!File.Exists(ExePath))
            throw new FileNotFoundException(
                $"TestApp not built at {ExePath}. Run: dotnet build -c {thisConfig} test/FlaUI.Mcp.TestApp");

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
