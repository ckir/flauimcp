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
