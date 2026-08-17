using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using Xunit;
using Xunit.Abstractions;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>SP3 Task 13 Steps 2-3 — THE WALK-COST MEASUREMENTS. Measures; asserts no policy.
///
/// Spec §4.4 claims a ZERO-COST DEFAULT PATH: with no rules configured, an install that never opts in
/// must pay nothing for SP3 existing. The plan forbids claiming that anywhere without figures, so this
/// produces them.
///
/// ⚠ TWO ARMS MEASURED IN ONE PROCESS, INTERLEAVED (A,B,A,B,…) — deliberate and load-bearing. Separate
/// runs would each pay their own JIT + UIA first-touch warm-up and would sit on different machine load,
/// so the DIFFERENCE between them would be dominated by noise rather than by rule evaluation. Interleaving
/// cancels slow drift (thermal, background work) across both arms instead of charging it to whichever ran
/// second. The driver reports MEDIAN with min/max, never a lone number, so noise stays visible.
///
/// ⚠ THE 64 RULES ARE BUILT NOT TO MATCH. That is the WORST case, not a lenient one: Classify returns on
/// the first match, so a matching rule set exits early and would flatter the result. A never-matching set
/// forces all 64 predicates to be evaluated for every node — the true ceiling.
///
/// The PRE-SP3 baseline (plan Step 2) cannot be measured from this tree; it is measured by running this
/// same zero-rule arm in a worktree at the branch point 3b0ab8d, whose PerceptionManager has no classifier
/// parameter at all. This file therefore uses ONLY the pre-SP3 API surface for that arm (the 3-arg ctor),
/// so the identical harness compiles and runs in both trees.</summary>
[Trait("Category", "Desktop")]
public class RedactionWalkCostMeasurementTests : IClassFixture<TestAppFixture>
{
    private readonly TestAppFixture _app;
    private readonly ITestOutputHelper _out;

    public RedactionWalkCostMeasurementTests(TestAppFixture app, ITestOutputHelper output)
    {
        _app = app;
        _out = output;
    }

    private const int WarmupWalks = 3;
    private const int Samples = 12;

    /// <summary>64 GLOBAL rules that match nothing. Global:true skips the process-name predicate, so every
    /// rule reaches its pattern test on every node — do not "improve" this into a process-scoped set, which
    /// would short-circuit and stop measuring the ceiling.</summary>
    private static SensitivityClassifier WorstCaseRules() =>
        SensitivityClassifier.ForRules(Enumerable.Range(0, 64).Select(i => new RedactionRule(
            name: $"measure-{i}",
            processName: null,
            global: true,
            automationId: null,
            automationIdPattern: $"^zzz-no-such-automation-id-{i}$",
            namePattern: $"^zzz-no-such-name-{i}$")));

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }

    [Fact]
    public async Task MEASUREMENT_walk_cost_zero_rules_vs_64_global_rules()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var options = new SnapshotOptions();

        // The zero-rule arm constructs the manager EXACTLY as pre-SP3 code did — no classifier argument.
        // That is the default path every non-adopting install runs.
        PerceptionManager ZeroRule() => new(mgr, new RefRegistry(), new SnapshotCache());
        PerceptionManager SixtyFour() =>
            new(mgr, new RefRegistry(), new SnapshotCache(), classifier: WorstCaseRules());

        async Task<(double Ms, int Nodes)> WalkAsync(PerceptionManager pm)
        {
            var sw = Stopwatch.StartNew();
            var (_, model) = await pm.BuildModelAsync(handle, options, new RefRegistry());
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds, model.NodeCount);
        }

        // Warm-up: JIT, UIA first-touch and the first cross-process bind are one-time costs that would
        // otherwise land entirely on whichever arm ran first.
        for (int i = 0; i < WarmupWalks; i++) { await WalkAsync(ZeroRule()); await WalkAsync(SixtyFour()); }

        var zero = new List<double>();
        var many = new List<double>();
        int zeroNodes = 0, manyNodes = 0;

        for (int i = 0; i < Samples; i++)
        {
            var a = await WalkAsync(ZeroRule());
            zero.Add(a.Ms); zeroNodes = a.Nodes;

            var b = await WalkAsync(SixtyFour());
            many.Add(b.Ms); manyNodes = b.Nodes;
        }

        Assert.True(zeroNodes > 0, "walked zero nodes — the measurement did not run");
        Assert.Equal(zeroNodes, manyNodes); // both arms must walk the SAME tree or the comparison is void

        double mz = Median(zero), mm = Median(many);
        _out.WriteLine($"NODES={zeroNodes} SAMPLES={Samples} (warmup {WarmupWalks})");
        _out.WriteLine($"ZERO_RULE   median={mz:F1}ms min={zero.Min():F1} max={zero.Max():F1}");
        _out.WriteLine($"RULES_64    median={mm:F1}ms min={many.Min():F1} max={many.Max():F1}");
        _out.WriteLine($"DELTA       median={mm - mz:+0.0;-0.0}ms  ratio={mm / mz:F3}x");

        // Deliberately NOT an assertion about the answer — a timing threshold on a shared, RDP-attached
        // machine would be a flaky test, not a measurement. Task 13 says record the figures; the driver
        // writes them into ROADMAP.md under item 9.
    }

    /// <summary>SP3 Task 13 Step 2 — THE DEFAULT-PATH ARM, shaped to be comparable to the PRE-SP3 BASELINE.
    ///
    /// ⚠ WHY THIS EXISTS SEPARATELY FROM THE FACT ABOVE, which already measures a zero-rule arm. That arm is
    /// INTERLEAVED with 64-rule walks, so every one of its samples follows a 64-rule walk and inherits its
    /// allocation/GC state. The pre-SP3 baseline (which cannot interleave — SP3 does not exist there) runs
    /// its zero-rule walks BACK-TO-BACK. Comparing those two directly confounds "the cost of SP3 on the
    /// default path" with "the cost of the shape of the harness", and the first measured gap (~57ms median)
    /// was the same order as the effect being looked for. This arm removes the confound by matching the
    /// baseline EXACTLY: same 3 warm-ups, same 12 samples, back-to-back, same 3-arg ctor, same options.
    ///
    /// ⚠ RUN IT ALONE (--filter on this method name). Running it after the interleaved fact in the same
    /// process reintroduces the very state difference it exists to eliminate.</summary>
    [Fact]
    public async Task MEASUREMENT_default_path_shaped_like_the_pre_sp3_baseline()
    {
        using var dispatcher = new AutomationDispatcher();
        using var mgr = new WindowManager(dispatcher);
        var handle = await mgr.OpenByPidAsync(_app.Process.Id);

        var options = new SnapshotOptions();

        async Task<(double Ms, int Nodes)> WalkAsync()
        {
            // The 3-arg ctor — no classifier argument, exactly as pre-SP3 callers wrote it.
            var pm = new PerceptionManager(mgr, new RefRegistry(), new SnapshotCache());
            var sw = Stopwatch.StartNew();
            var (_, model) = await pm.BuildModelAsync(handle, options, new RefRegistry());
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds, model.NodeCount);
        }

        for (int i = 0; i < WarmupWalks; i++) await WalkAsync();

        var times = new List<double>();
        int nodes = 0;
        for (int i = 0; i < Samples; i++)
        {
            var r = await WalkAsync();
            times.Add(r.Ms); nodes = r.Nodes;
        }

        Assert.True(nodes > 0, "walked zero nodes — the measurement did not run");

        _out.WriteLine($"NODES={nodes} SAMPLES={Samples} (warmup {WarmupWalks})");
        _out.WriteLine($"SP3_DEFAULT median={Median(times):F1}ms min={times.Min():F1} max={times.Max():F1}");
    }
}
