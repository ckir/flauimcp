using System.Text.Json;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Perception;

/// <summary>ROADMAP opportunistic item 6, the desktop_list_windows half — the wire shape of the Hint
/// field. Headless on purpose: the tool projects the WindowInfo RECORD straight through
/// (WindowTools.cs:27, `ToolResponse.Ok(await _windows.ListWindowsAsync(...))`), so the shape is decided
/// by the record's attributes plus ToolResponse's serializer, not by anything a live desktop contributes.
/// Gating this on a Windows Terminal window actually being open would make a CI tripwire depend on the
/// operator's session — and it would test the enumeration, not the projection.
///
/// Both the real record and the real serializer are exercised here; nothing about the payload is
/// re-declared locally, so the test cannot pass against a projection that has drifted.
///
/// Two facts, and the SECOND is the one that surprises people: unlike desktop_wait_for's anonymous
/// projection — which emits its nulls explicitly (ToolProjectionShapeTests) — Hint carries
/// JsonIgnoreCondition.WhenWritingNull (WindowManager.cs:20), so it is OMITTED, and it keeps the record's
/// PascalCase name because ToolResponse sets no naming policy. An agent that probes for a lowercase
/// "hint", or expects a null, finds neither.</summary>
public class ListWindowsProjectionShapeTests
{
    private static JsonElement FirstWindow(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement[0].Clone();
    }

    [Fact]
    public void Hint_reaches_the_wire_as_PascalCase_for_a_multiplexer_window()
    {
        var listing = new[]
        {
            new WindowInfo("PowerShell", "WindowsTerminal", 1234, IsForeground: true,
                Hint: MultiplexerHint.For("WindowsTerminal")),
        };

        var w = FirstWindow(ToolResponse.Ok(listing));

        Assert.True(w.TryGetProperty("Hint", out var hint));
        Assert.Equal(JsonValueKind.String, hint.ValueKind);
        Assert.Contains("ONLY the active tab", hint.GetString());
        Assert.False(w.TryGetProperty("hint", out _)); // no camelCase naming policy is applied
    }

    [Fact]
    public void Hint_is_omitted_entirely_for_an_ordinary_window()
    {
        var listing = new[]
        {
            new WindowInfo("Untitled - Notepad", "notepad", 4321, IsForeground: false,
                Hint: MultiplexerHint.For("notepad")),
        };

        var w = FirstWindow(ToolResponse.Ok(listing));

        // Omitted, NOT null — the opposite of desktop_wait_for's diagnostic keys.
        Assert.False(w.TryGetProperty("Hint", out _));
        Assert.Equal("Untitled - Notepad", w.GetProperty("Title").GetString());
    }

    [Fact]
    public void The_opt_in_fields_are_omitted_until_asked_for()
    {
        // Bounds/ZOrder/Handle share Hint's omit-when-null rule, so the same drift breaks all four at
        // once. Pinning them together makes that failure legible instead of arriving one bug report
        // at a time.
        var listing = new[] { new WindowInfo("Calculator", "CalculatorApp", 9, IsForeground: false) };

        var w = FirstWindow(ToolResponse.Ok(listing));

        Assert.False(w.TryGetProperty("Bounds", out _));
        Assert.False(w.TryGetProperty("ZOrder", out _));
        Assert.False(w.TryGetProperty("Handle", out _));
        Assert.False(w.TryGetProperty("Hint", out _));
    }
}
