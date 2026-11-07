// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>Spec §6.2. Every string parameter of every tool, and every string property of a complex
/// parameter type, is classified on purpose — in BOTH directions.</summary>
public class ToolParameterCoverageTests
{
    // Parameter NAMES that are structural in every tool that has them.
    private static readonly HashSet<string> StructuralNames = new(StringComparer.Ordinal)
    {
        "window", "ref", "root", "scope", "scopeRef", "endWindow", "baselineSnapshotId", "snapshotId",
        "subscriptionId", "wakeId", "direction", "action", "button", "modifiers", "chord", "events",
        "nameMatch", "controlType", "matchMode", "output", "until", "by", "path",
    };

    // UI text, as tool/param.
    private static readonly HashSet<string> UiText = new(StringComparer.Ordinal)
    {
        "desktop_find/automationId", "desktop_find/name",
        "desktop_find_text/query", "desktop_wait_for_text/query",
        "desktop_tray_action/icon", "desktop_tray_action/menuItem",
        "desktop_open_window/value", "desktop_wait_for/value", "desktop_wait_for_stable/value",
    };

    // Content on purpose (hashed): carry NEITHER attribute.
    private static readonly HashSet<string> Content = new(StringComparer.Ordinal)
    {
        "desktop_type/text", "desktop_paste_text/text", "desktop_clipboard_set/text",
        "desktop_set_value/value", "desktop_launch_app/args", "desktop_wait_for/equals",
    };

    private static IEnumerable<(string Tool, ParameterInfo P)> StringParams()
    {
        foreach (var t in typeof(ToolResponse).Assembly.GetTypes()
                     .Where(t => t.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() is not null))
        foreach (var m in t.GetMethods().Where(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null))
        foreach (var p in m.GetParameters())
        {
            var pt = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            if (pt == typeof(string) || pt == typeof(string[])) yield return (ToolParameterCatalog.SnakeCase(m.Name), p);
        }
    }

    [Fact]
    public void The_catalog_sees_all_53_tools_with_desktop_names()
    {
        var c = ToolParameterCatalog.FromAssembly(typeof(ToolResponse).Assembly);
        Assert.Equal(53, c.Tools.Count);
        Assert.All(c.Tools, t => Assert.Matches("^desktop_[a-z]+(_[a-z]+)*$", t.Name));
        Assert.NotNull(c.Find("desktop_get_text"));
        Assert.NotNull(c.Find("desktop_type"));
    }

    [Fact]
    public void Every_string_parameter_is_classified_exactly_as_the_oracle_says()
    {
        var problems = new List<string>();
        foreach (var (tool, p) in StringParams())
        {
            var key = $"{tool}/{p.Name}";
            var actual = ToolParameterCatalog.ClassOf(p);
            ParamClass expected =
                Content.Contains(key) ? ParamClass.Content
                : UiText.Contains(key) ? ParamClass.UiText
                : StructuralNames.Contains(p.Name!) ? ParamClass.Structural
                : (ParamClass)(-1);
            if ((int)expected == -1) problems.Add($"{key}: not in the oracle - classify it in this test on purpose");
            else if (actual != expected) problems.Add($"{key}: is {actual}, oracle says {expected}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Content_parameters_carry_neither_attribute()
    {
        // Reverse direction: marking a content parameter raw to silence the guard must fail.
        var found = StringParams().Where(x => Content.Contains($"{x.Tool}/{x.P.Name}")).ToList();
        Assert.Equal(Content.Count, found.Count);
        Assert.All(found, x =>
        {
            Assert.False(x.P.IsDefined(typeof(LogStructuralAttribute), false), $"{x.Tool}/{x.P.Name}");
            Assert.False(x.P.IsDefined(typeof(LogUiTextAttribute), false), $"{x.Tool}/{x.P.Name}");
        });
    }

    [Fact]
    public void Every_string_property_of_a_complex_parameter_type_is_classified()
    {
        var expected = new Dictionary<string, ParamClass>
        {
            ["AutomationId"] = ParamClass.UiText, ["Name"] = ParamClass.UiText,
            ["NameMatch"] = ParamClass.Structural, ["ControlType"] = ParamClass.Structural, ["Scope"] = ParamClass.Structural,
        };
        var complexTypes = typeof(ToolResponse).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null)
            .SelectMany(m => m.GetParameters())
            .Select(p => Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType)
            .Where(t => !t.IsPrimitive && t != typeof(string) && !t.IsArray && t != typeof(ModelContextProtocol.Server.McpServer))
            .Distinct().ToList();
        Assert.Equal(new[] { typeof(FlaUI.Mcp.Core.Perception.Selector) }, complexTypes);
        foreach (var prop in typeof(FlaUI.Mcp.Core.Perception.Selector).GetProperties().Where(p => p.PropertyType == typeof(string)))
            Assert.Equal(expected[prop.Name], ToolParameterCatalog.ClassOf(prop));
    }

    [Fact]
    public void Only_desktop_key_is_a_foreground_target()
    {
        var c = ToolParameterCatalog.FromAssembly(typeof(ToolResponse).Assembly);
        Assert.Equal(new[] { "desktop_key" }, c.Tools.Where(t => t.ForegroundTarget).Select(t => t.Name).ToArray());
    }
}
