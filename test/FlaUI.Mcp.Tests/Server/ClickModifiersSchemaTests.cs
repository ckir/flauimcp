using System.ComponentModel;
using System.Linq;
using System.Reflection;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

// fix-the-tool: docs/fix-the-tool-backlog/click-missing-modifiers-param.md — FIXED: DesktopClick now
// exposes a `modifiers` parameter wired into the existing _guard.MouseClick modifiers slot.
public class ClickModifiersSchemaTests
{
    [Fact]
    public void DesktopClick_exposes_a_modifiers_parameter_documented_in_its_description()
    {
        // Arrange + Invoke: read the real tool method's Description attribute and parameter list — the
        // exact source the MCP input schema is generated from, no live window needed.
        var method = typeof(InputTools).GetMethod(nameof(InputTools.DesktopClick), BindingFlags.Public | BindingFlags.Instance)!;
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        var paramNames = method.GetParameters().Select(p => p.Name).ToArray();

        Assert.Contains("modifiers", description, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("modifiers", paramNames);
    }
}
