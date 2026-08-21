using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

/// <summary>Structural invariant: trap-class facts must be present in the tool DESCRIPTIONS, which are
/// the only surface that re-enters context every time a tool loads and cannot be held stale, skimmed or
/// truncated. The assertions target the IMPERATIVE clause, not a keyword — a passive "X may occur" note
/// was already present when the motivating failure happened, so a keyword check would pass on text that
/// demonstrably does not change behaviour.</summary>
public class ToolTrapFactInvariantTests
{
    /// MEASURED BY REFLECTION across all 49 tools — a source scan is NOT reliable here, it silently
    /// missed 11 tools including the actual ceiling. The longest description is WatchTools.DesktopWatch
    /// at 1403 chars. Spec §7 criterion 5 sets the budget at or just above the measured longest, so
    /// hoisting cannot silently metastasize. Raising this number to make a new description fit is
    /// exactly the failure it exists to catch — trim the description instead.
    private const int DescriptionBudget = 1500;

    /// <summary>Per-tool ceilings, each with its reason. ⚠⚠ THIS IS NOT AN ESCAPE HATCH, AND THE
    /// DIFFERENCE IS THE WHOLE DESIGN: the general budget above is UNCHANGED for the other 48 tools, an
    /// excepted tool still has a HARD ceiling rather than none, and adding an entry here is a visible,
    /// reviewable act that has to be argued for in this comment. Raising `DescriptionBudget` itself to
    /// fit one tool is what the guard forbids, because it silently loosens all 49 at once.
    ///
    /// <para>`ScreenshotTools.DesktopScreenshot` — OPERATOR DECISION, 2026-08-22, on measurement. Item 8
    /// gave this one tool two capture BACKENDS where every other tool has one, and the caller must be
    /// told things that simply did not exist when the 1500 figure was set just above the then-longest
    /// description (1403, which carried none of this):</para>
    /// <list type="bullet">
    /// <item>which backend produced the image, and that it must be CHECKED rather than inferred from the
    /// scope requested, because window scope silently falls back;</item>
    /// <item>that coordinates from a `printWindow` capture are NOT clickable — the window may be occluded
    /// or off-screen, so a click computed from them lands on whatever is really drawn there. This is a
    /// WRONG-ACTION hazard, not a misread-data one;</item>
    /// <item>that an occluded Chromium-family window can suspend rendering and return an arbitrarily old
    /// frame that re-capturing cannot refresh (Phase 0 gate answer 3, accepted as a documentation item
    /// because no mitigation exists);</item>
    /// <item>that `unmaskedProcesses`/`maskEscalations`/`escalated` describe a WHOLE-DESKTOP walk on a
    /// fallback — the privacy-relevant one, since it changes what an entry means.</item>
    /// </list>
    /// <para>MEASURED before this exception was added: the description compressed as hard as it could be
    /// without dropping one of those facts is 1711 chars. The only single omissions that reach 1500 are
    /// the freshness limit (exactly 1500) or the clickability warning (1559, still over) — both
    /// behaviour-changing. The ceiling here is set just above the measured 1711, on the same principle as
    /// the general budget, so this tool cannot drift either.</para></summary>
    private static readonly System.Collections.Generic.Dictionary<string, int> PerToolBudget = new()
    {
        ["ScreenshotTools.DesktopScreenshot"] = 1750,
    };

    private static int BudgetFor(string tool) =>
        PerToolBudget.TryGetValue(tool, out var b) ? b : DescriptionBudget;

    private static string Description(Type type, string method)
    {
        var m = type.GetMethod(method) ?? throw new InvalidOperationException($"{type.Name}.{method} not found");
        var d = m.GetCustomAttribute<DescriptionAttribute>();
        Assert.True(d is not null, $"{type.Name}.{method} has no [Description]");
        return d!.Description;
    }

    [Theory]
    [InlineData(typeof(WindowTools), nameof(WindowTools.DesktopListWindows))]
    [InlineData(typeof(ContentTools), nameof(ContentTools.DesktopReadTerminalTab))]
    public void Terminal_tab_trap_is_stated_as_an_imperative(Type type, string method)
    {
        var text = Description(type, method);
        Assert.Contains("launcher, not the program", text, StringComparison.Ordinal);
        Assert.Contains("read every candidate", text, StringComparison.Ordinal);
    }

    /// <summary>Any tool that talks about tabIndex must name where a VALID one comes from. SP2 added
    /// desktop_list_terminal_tabs precisely because a desktop_snapshot ordinal is NOT usable as a tabIndex —
    /// four of the five snapshot-walk filters can drop a TabItem — yet desktop_read_terminal_tab's own
    /// description and its tabIndex parameter both kept telling callers to enumerate via desktop_snapshot,
    /// which an AGY-CAPSTONE round found only after the skill, the ROADMAP and the out-of-range recovery had
    /// all been corrected. That is the recurring shape of this whole subproject: the fix lands, and a
    /// PARALLEL user-facing surface still says the old thing.
    ///
    /// This sweeps by REFLECTION rather than naming the two known tools, so a THIRD tool that starts talking
    /// about tabIndex inherits the rule for free. desktop_list_terminal_tabs is exempt from naming itself.
    /// Deliberately a POSITIVE assertion ("must name the canonical source") rather than a ban on the string
    /// "desktop_snapshot" — the corrected description mentions it on purpose, to warn against it.
    ///
    /// ⚠ PARAMETER descriptions are swept too, and that is not incidental. The first version of this test
    /// read only the METHOD's [Description] — so a parameter description could have kept saying "from a
    /// desktop_snapshot of the tab strip" and this test would have passed. That is exactly the surface the
    /// original defect lived on (BOTH the tool description and the tabIndex parameter said it), and the
    /// mutation that "verified" the first version only mutated the method text, so it never exercised the
    /// gap. Caught by an AGY-CAPSTONE Mechanism Gamer seat. A tripwire is only as wide as the surface it
    /// actually reads.</summary>
    [Fact]
    public void Every_description_mentioning_tabIndex_names_the_canonical_source()
    {
        const string Canonical = "desktop_list_terminal_tabs";
        const string Exempt = $"{nameof(ContentTools)}.{nameof(ContentTools.DesktopListTerminalTabs)}";

        // EACH surface is judged ON ITS OWN. Concatenating method + params would let the method's mention of
        // the canonical source mask a regressed PARAMETER — which is precisely the shape of the original
        // defect, where both surfaces said the wrong thing independently.
        var surfaces = typeof(WindowTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttributesData().Any(d => d.AttributeType == typeof(McpServerToolAttribute)))
                .SelectMany(m => new[] { (Tool: $"{t.Name}.{m.Name}", Where: "description",
                        Text: m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "") }
                    .Concat(m.GetParameters().Select(p => (Tool: $"{t.Name}.{m.Name}", Where: $"param '{p.Name}'",
                        Text: p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "")))));

        var offenders = surfaces
            .Where(s => s.Tool != Exempt)
            // Trigger on a surface that talks about a tab ordinal AND points at the snapshot walk. The
            // wording varies ("tabIndex" in the tool text, "tab ordinal" in the parameter), so match both.
            .Where(s => s.Text.Contains("tabIndex", StringComparison.Ordinal)
                     || s.Text.Contains("tab ordinal", StringComparison.Ordinal))
            .Where(s => s.Text.Contains("desktop_snapshot", StringComparison.Ordinal))
            // Naming the canonical source is what makes a desktop_snapshot mention a WARNING rather than
            // advice — the shipped description deliberately says "NOT from a desktop_snapshot".
            .Where(s => !s.Text.Contains(Canonical, StringComparison.Ordinal))
            .Select(s => $"{s.Tool} ({s.Where})")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"these tool descriptions mention tabIndex without naming {Canonical}, the only source whose "
            + $"index space matches by construction:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void No_tool_description_exceeds_the_budget()
    {
        var over = typeof(WindowTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttributesData().Any(d => d.AttributeType == typeof(McpServerToolAttribute)))
                .Select(m => (Name: $"{t.Name}.{m.Name}", Len: m.GetCustomAttribute<DescriptionAttribute>()?.Description.Length ?? 0)))
            .Where(x => x.Len > BudgetFor(x.Name))
            .Select(x => $"{x.Name} = {x.Len} chars (ceiling {BudgetFor(x.Name)})")
            .ToList();

        Assert.True(over.Count == 0,
            $"tool descriptions over their ceiling ({DescriptionBudget} unless listed in PerToolBudget) "
            + "— hoisting must not metastasize:\n" + string.Join("\n", over));

        // ⚠ AN EXCEPTION FOR A TOOL THAT NO LONGER NEEDS ONE IS ITSELF ROT. Without this, a later trim
        // could bring the description back under 1500 and the raised ceiling would linger, quietly
        // re-opening the room the guard exists to deny. An entry must EARN its place every run.
        var unnecessary = PerToolBudget.Keys
            .Where(name =>
            {
                var len = typeof(WindowTools).Assembly.GetTypes()
                    .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.GetCustomAttributesData().Any(d => d.AttributeType == typeof(McpServerToolAttribute)))
                        .Select(m => (Name: $"{t.Name}.{m.Name}", Len: m.GetCustomAttribute<DescriptionAttribute>()?.Description.Length ?? 0)))
                    .Where(x => x.Name == name)
                    .Select(x => (int?)x.Len)
                    .FirstOrDefault();
                return len is not null && len <= DescriptionBudget;
            })
            .ToList();

        Assert.True(unnecessary.Count == 0,
            "these tools now fit the general budget - REMOVE their PerToolBudget entry rather than "
            + $"leaving headroom nobody is using:\n{string.Join("\n", unnecessary)}");
    }
}
