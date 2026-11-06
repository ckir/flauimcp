// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.ComponentModel;
using System.Reflection;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Tray;

public class TrayToolsContractTests
{
    private static MethodInfo M(string name) => typeof(TrayTools).GetMethod(name)!;
    private static string Desc(string name) => M(name).GetCustomAttribute<DescriptionAttribute>()!.Description;
    private static bool Explicit(string name, string arg)
        => M(name).GetCustomAttributesData().First(d => d.AttributeType == typeof(McpServerToolAttribute))
            .NamedArguments.Any(a => a.MemberName == arg && a.TypedValue.Value is true);

    [Theory]
    [InlineData("DesktopOpenTray")]
    [InlineData("DesktopListTrayIcons")]
    public void The_read_tools_are_ReadOnly(string name) => Assert.True(Explicit(name, "ReadOnly"));

    [Fact]
    public void Tray_action_is_Destructive() => Assert.True(Explicit("DesktopTrayAction", "Destructive"));

    [Fact]
    public void Tray_action_defaults_timeoutMs_to_4000()
        => Assert.Equal(4000, M("DesktopTrayAction").GetParameters().Single(p => p.Name == "timeoutMs").DefaultValue);

    [Fact]
    public void Open_tray_description_routes_right_click_menus_to_tray_action()
        => Assert.Contains("desktop_tray_action", Desc("DesktopOpenTray"));

    [Fact]
    public void Tray_action_description_tells_the_agent_to_check_matchedIcon()
        => Assert.Contains("matchedIcon", Desc("DesktopTrayAction"));

    [Fact]
    public void List_description_states_the_tooltip_redaction_limit()
        => Assert.Contains("explorer", Desc("DesktopListTrayIcons"));

    [Fact]
    public async Task Tray_action_with_an_unknown_action_is_InvalidArguments_before_any_dependency_is_touched()
    {
        var tools = new TrayTools(tray: null!, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var json = await tools.DesktopTrayAction("ShareX", "double_click");
        Assert.Contains("InvalidArguments", json);
    }

    [Fact]
    public async Task Tray_action_with_a_negative_timeout_is_InvalidArguments()
    {
        var tools = new TrayTools(tray: null!, new ServerOptions(ReadOnly: false, AllowElevation: false));
        var json = await tools.DesktopTrayAction("ShareX", "left_click", timeoutMs: -1);
        Assert.Contains("InvalidArguments", json);
    }

    [Fact]
    public void Desktop_click_description_routes_tray_right_clicks_to_tray_action()
        => Assert.Contains("desktop_tray_action",
            typeof(InputTools).GetMethod(nameof(InputTools.DesktopClick))!.GetCustomAttribute<DescriptionAttribute>()!.Description);
}
