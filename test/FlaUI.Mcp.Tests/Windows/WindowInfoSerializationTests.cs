using System.Text.Json;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Windows;

// Headless: pure wire-shape check on the WindowInfo record (Category != Desktop).
public class WindowInfoSerializationTests
{
    [Fact]
    public void Handle_is_omitted_when_null_default_response_is_unchanged()
    {
        var wi = new WindowInfo("Untitled - Notepad", "notepad", 1234, true);
        var json = JsonSerializer.Serialize(wi);
        Assert.DoesNotContain("Handle", json);
        Assert.DoesNotContain("handle", json);
    }

    [Fact]
    public void Handle_is_emitted_when_set()
    {
        var wi = new WindowInfo("Untitled - Notepad", "notepad", 1234, true, Handle: "w7");
        var json = JsonSerializer.Serialize(wi);
        Assert.Contains("\"Handle\":\"w7\"", json);
    }
}
