using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;
using Xunit.Abstractions;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP3 Task 13 Step 1 — THE PROCESS-HOMOGENEITY MEASUREMENT. Measures; asserts no policy.
///
/// Two things rest on the same unverified assumption that every node under a window root belongs to the
/// root's process:
///   1. SP3's per-walk `processName` hoist (read ONCE per walk, then applied to every node's rule match).
///   2. ⚠ The SHIPPED whole-window denylist, which PREDATES SP3 — it decides "refuse this whole window"
///      from the ROOT's process. If a window's nodes are not all the root's process, then content owned by
///      a DENIED process embedded inside an ALLOWED window is served. That is a live hole in today's
///      security floor, not a new SP3 risk.
///
/// A cross-process host is not exotic: Windows' own `ApplicationFrameHost.exe` hosts UWP windows whose
/// frame is one process and whose content is another (Settings, Windows Security). Browsers and Electron
/// do the same. This walks every top-level window it can bind and reports the distinct owning process ids
/// per window, so the answer is a measured number rather than an assumption either way.</summary>
[Trait("Category", "Desktop")]
public class ProcessHomogeneityMeasurementTests
{
    private readonly ITestOutputHelper _out;
    public ProcessHomogeneityMeasurementTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task MEASUREMENT_distinct_owning_processes_per_window()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);

        var windows = await mgr.ListWindowsAsync(includeBounds: false, includeHandles: true);
        Assert.NotEmpty(windows); // a measurement that silently measured nothing is the failure mode here

        var heterogeneous = new List<string>();
        int measured = 0;

        foreach (var w in windows.Where(w => w.Handle is not null))
        {
            // ⚠ MEASURE BY NAME, NOT ONLY BY PID. Both mechanisms under test key on the process NAME
            // (PerceptionPolicy.IsDenied(procName); SP3's classifier matches RedactionRule.ProcessName), so
            // PID heterogeneity alone proves nothing about either. MEASURED: a Chromium window's nodes span
            // several PIDs that are ALL "msedge" — the hoist and the denylist are both correct there. The
            // dangerous shape is a NAME difference, e.g. a WebView2 host where "msedgewebview2" content is
            // embedded in a differently-named host app.
            (int RootPid, Dictionary<int, int> ByPid, int Nodes)? r = null;
            try
            {
                r = await mgr.RunWithWindowAndDesktopAsync(new WindowHandle(w.Handle!), (win, _) =>
                {
                    int rootPid = win.Properties.ProcessId.ValueOrDefault;
                    var byPid = new Dictionary<int, int>();
                    int nodes = 0;
                    foreach (var d in win.FindAllDescendants())
                    {
                        nodes++;
                        int pid;
                        try { pid = d.Properties.ProcessId.ValueOrDefault; } catch { continue; }
                        byPid[pid] = byPid.TryGetValue(pid, out var c) ? c + 1 : 1;
                    }
                    return (rootPid, byPid, nodes);
                });
            }
            catch { continue; } // a window that closed or cannot be bound is not a measurement failure

            if (r is null) continue;
            measured++;
            var (rootPid, byPid, nodes) = r.Value;

            // Resolve each PID to its process NAME — that is the property both mechanisms actually use.
            string NameOf(int pid)
            {
                try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
                catch { return $"<gone:{pid}>"; }
            }
            string rootName = NameOf(rootPid);
            var names = byPid.Keys.Where(p => p != 0).Select(NameOf)
                              .Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
            var foreignNames = names.Where(n => !string.Equals(n, rootName, System.StringComparison.OrdinalIgnoreCase)).ToList();

            string summary =
                $"{w.ProcessName} (root pid {rootPid}, name '{rootName}') '{w.Title}': {nodes} node(s), " +
                $"pids [{string.Join(", ", byPid.Keys.OrderBy(k => k))}], names [{string.Join(", ", names)}]";
            _out.WriteLine(summary);

            // ONLY a NAME difference is a finding. A PID difference under one name (Chromium's child
            // processes) leaves both the denylist and the hoist correct.
            if (foreignNames.Count > 0)
                heterogeneous.Add(summary + $"  <== FOREIGN NAME(S): {string.Join(", ", foreignNames)}");
        }

        Assert.True(measured > 0, "bound no window at all — the measurement did not run");

        _out.WriteLine($"---- measured {measured} window(s); {heterogeneous.Count} heterogeneous ----");
        foreach (var h in heterogeneous) _out.WriteLine(h);

        // Deliberately NOT an assertion about the answer. Task 13 says: record the result either way.
        // The number is consumed by the driver and written into ROADMAP.md under item 9.
    }
}
