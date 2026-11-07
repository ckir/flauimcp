// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Mcp.Server.Logging;
using ModelContextProtocol.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

[McpServerToolType]
public sealed class FakeTools
{
    [McpServerTool] public string DesktopFakeType([LogStructural] string window, string text, [LogStructural] string? @ref = null,
        Selector? selector = null, int timeoutMs = 4000, bool verify = true) => "";
    [McpServerTool, LogForegroundTarget] public string DesktopFakeKey([LogStructural] string chord, [LogStructural] string? window = null) => "";
    [McpServerTool] public string DesktopFakeFind([LogStructural] string window, [LogUiText] string? name = null,
        [LogStructural] int textLength = 0, [LogStructural] string[]? modifiers = null) => "";
}

public class ArgumentClassifierTests
{
    private static readonly ToolParameterCatalog Catalog = ToolParameterCatalog.FromTypes(new[] { typeof(FakeTools) });
    private static readonly ContentHasher Hasher = new(new byte[32]);

    private static IDictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static (JsonObject Args, bool Content) Run(string tool, string json, bool sensitive = false) =>
        ArgumentClassifier.Classify(Catalog.Find(tool), Args(json), sensitive, Hasher);

    private static bool IsMasked(JsonNode? n) => n is JsonObject o && o.ContainsKey("len") && o.ContainsKey("sha8");

    [Fact]
    public void Names_are_snake_case_of_the_method()
    {
        Assert.NotNull(Catalog.Find("desktop_fake_type"));
        Assert.True(Catalog.Find("desktop_fake_key")!.ForegroundTarget);
        Assert.False(Catalog.Find("desktop_fake_type")!.ForegroundTarget);
    }

    [Fact]
    public void Structural_raw_content_hashed_numbers_raw()
    {
        var (a, content) = Run("desktop_fake_type", """{"window":"w3","text":"hunter2","ref":"e5","timeoutMs":250,"verify":false}""");
        Assert.Equal("w3", a["window"]!.GetValue<string>());
        Assert.Equal("e5", a["ref"]!.GetValue<string>());
        Assert.True(IsMasked(a["text"]));
        Assert.Equal(7, a["text"]!["len"]!.GetValue<int>());
        Assert.Equal(250, a["timeoutMs"]!.GetValue<int>());
        Assert.False(a["verify"]!.GetValue<bool>());
        Assert.True(content);
    }

    [Fact]
    public void Ui_text_raw_unless_sensitive()
    {
        Assert.Equal("Save As", Run("desktop_fake_find", """{"window":"w1","name":"Save As"}""").Args["name"]!.GetValue<string>());
        Assert.True(IsMasked(Run("desktop_fake_find", """{"window":"w1","name":"Save As"}""", sensitive: true).Args["name"]));
        Assert.False(Run("desktop_fake_find", """{"window":"w1","name":"Save As"}""").Content);
    }

    [Fact]
    public void Selector_is_classified_per_property()
    {
        var json = """{"window":"w1","text":"x","selector":{"name":"OK","automationId":"btnOk","controlType":"Button","nameMatch":"eq","ignoreCase":true,"extra":"zzz"}}""";
        var sel = Run("desktop_fake_type", json).Args["selector"]!.AsObject();
        Assert.Equal("OK", sel["name"]!.GetValue<string>());
        Assert.Equal("btnOk", sel["automationId"]!.GetValue<string>());
        Assert.Equal("Button", sel["controlType"]!.GetValue<string>());
        Assert.True(sel["ignoreCase"]!.GetValue<bool>());
        Assert.True(IsMasked(sel["extra"]));                       // unknown property: content
        var masked = Run("desktop_fake_type", json, sensitive: true).Args["selector"]!.AsObject();
        Assert.True(IsMasked(masked["name"]));
        Assert.Equal("Button", masked["controlType"]!.GetValue<string>());
    }

    [Fact]
    public void Classification_is_by_attribute_never_by_name()
    {
        // Distractor: a structural parameter whose NAME looks like content stays raw.
        Assert.Equal(12, Run("desktop_fake_find", """{"window":"w1","textLength":12}""").Args["textLength"]!.GetValue<int>());
        // A string sent to a numeric parameter is not trusted as structural.
        Assert.True(IsMasked(Run("desktop_fake_find", """{"window":"w1","textLength":"secret"}""").Args["textLength"]));
    }

    [Fact]
    public void Unknown_tool_and_unknown_param_fail_closed()
    {
        var (a, content) = ArgumentClassifier.Classify(null, Args("""{"window":"w1","q":"x","n":3}"""), false, Hasher);
        Assert.True(IsMasked(a["window"]));
        Assert.True(IsMasked(a["q"]));
        Assert.Equal(3, a["n"]!.GetValue<int>());
        Assert.True(content);
        Assert.True(IsMasked(Run("desktop_fake_type", """{"window":"w1","bogus":"x"}""").Args["bogus"]));
    }

    [Fact]
    public void Structural_string_array_raw()
    {
        var a = Run("desktop_fake_find", """{"window":"w1","modifiers":["Ctrl","Shift"]}""").Args;
        Assert.Equal("Ctrl", a["modifiers"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Null_and_empty_content_do_not_count_as_content()
    {
        Assert.False(Run("desktop_fake_type", """{"window":"w1","text":null}""").Content);
    }
}
