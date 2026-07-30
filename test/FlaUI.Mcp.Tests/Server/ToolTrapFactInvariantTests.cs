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
    /// "desktop_snapshot" — the corrected description mentions it on purpose, to warn against it.</summary>
    [Fact]
    public void Every_description_mentioning_tabIndex_names_the_canonical_source()
    {
        const string Canonical = "desktop_list_terminal_tabs";
        var offenders = typeof(WindowTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttributesData().Any(d => d.AttributeType == typeof(McpServerToolAttribute)))
                .Select(m => (Name: $"{t.Name}.{m.Name}", Text: m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "")))
            .Where(x => x.Text.Contains("tabIndex", StringComparison.Ordinal))
            .Where(x => x.Name != $"{nameof(ContentTools)}.{nameof(ContentTools.DesktopListTerminalTabs)}")
            .Where(x => !x.Text.Contains(Canonical, StringComparison.Ordinal))
            .Select(x => x.Name)
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
            .Where(x => x.Len > DescriptionBudget)
            .Select(x => $"{x.Name} = {x.Len} chars")
            .ToList();

        Assert.True(over.Count == 0,
            $"tool descriptions over {DescriptionBudget} chars — hoisting must not metastasize:\n"
            + string.Join("\n", over));
    }
}
