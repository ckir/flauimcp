using System.ComponentModel;
using System.Linq;
using System.Reflection;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

// fix-the-tool: docs/fix-the-tool-backlog/click-missing-modifiers-param.md
[Trait("Category", "KnownDefect")]
public class ClickModifiersSchemaTests
{
    [Fact]
    public void DesktopClick_schema_has_no_modifiers_parameter_despite_its_description_promising_one()
    {
        // Arrange + Invoke: read the real tool method's Description attribute and parameter list — the
        // exact source the MCP input schema is generated from, no live window needed.
        var method = typeof(InputTools).GetMethod(nameof(InputTools.DesktopClick), BindingFlags.Public | BindingFlags.Instance)!;
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        var paramNames = method.GetParameters().Select(p => p.Name).ToArray();

        Assert.Contains("modifiers", description, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("modifiers", paramNames);

        // KNOWN DEFECT: the description promises "modifiers optional" but the schema has no such
        // parameter, so Ctrl+click / Shift+click is impossible via desktop_click.
        Assert.Fail("click-missing-modifiers-param: DesktopClick's description says \"modifiers optional\" " +
            $"(\"{description}\") but its actual parameters are [{string.Join(", ", paramNames)}] — no " +
            "modifiers parameter exists, so Ctrl+click/Shift+click is impossible; correct behavior not " +
            "asserted yet — see docs/fix-the-tool-backlog/click-missing-modifiers-param.md");
    }
}
