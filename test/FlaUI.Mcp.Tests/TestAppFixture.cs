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

        // ⚠ WAIT FOR THE WINDOW, DO NOT SLEEP AT IT. This was `Thread.Sleep(500)` with the comment "let the
        // window realize" — a fixed guess that holds on an idle machine and fails under suite load, which
        // is exactly when it matters. MEASURED: CheckRedactionRulesLiveTests.List_windows_reports_the_
        // running_fixture_by_pid fails reproducibly when this class runs after ~6 other TestApp-launching
        // classes, and passes in isolation. It reproduces identically at an earlier commit, so it is an
        // order-dependent race in the FIXTURE, not a regression in anything it tests.
        //
        // WindowManager.EnumTopLevel only reports a window that is IsWindowVisible with a non-empty title,
        // so a not-yet-realized window is silently ABSENT from the listing rather than late — the failure
        // presents as "the app is not running", which is the most misleading shape it could take.
        //
        // MainWindowHandle is the right signal because it becomes non-zero only once a real top-level
        // window exists. Refresh() is required: Process caches it, so polling without it re-reads a stale 0
        // forever. The deadline keeps a genuinely failed launch a fast failure rather than a hang.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Process.Refresh();
            if (Process.HasExited || Process.MainWindowHandle != IntPtr.Zero) break;
            Thread.Sleep(25);
        }
        // ⚠ KEEP THE FULL 500ms SETTLE. The defect being fixed above was "does not WAIT for the window",
        // not "sleeps too long" — and shortening this to 100ms immediately broke a different test.
        // PopupRootCoverageTests right-clicks at a COORDINATE, and its own arrange-failure message names
        // the hazard: "a sibling TestApp window stacked at the same default position can swallow it". The
        // extra ~400ms is what lets the PREVIOUS class's dying TestApp window leave the screen before the
        // next class clicks at that same default position. Returning sooner is not a virtue here.
        //
        // So: the handle poll makes window presence DETERMINISTIC, and this preserves the original timing
        // envelope. Both are needed; neither replaces the other.
        Thread.Sleep(500);
    }

    public void Dispose()
    {
        // Wait for full exit, not just Kill: a half-torn-down TestApp window left in the desktop tree
        // makes the NEXT test's window enumeration (ListWindowsAsync) block on the dying window's
        // UIA property read (STA, no timeout) — which manifested as a full-suite hang.
        try
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            Process.WaitForExit(3000);
        }
        catch { /* already gone */ }
    }
}
