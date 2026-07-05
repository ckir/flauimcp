using FlaUI.Mcp.Server.Attention;
using Xunit;

namespace FlaUI.Mcp.Tests.Attention;

public class TtsSignalTests
{
    [Fact]
    public void Utterance_names_the_target_app_only()
    {
        var line = TtsSignal.Utterance("Character Map");
        Assert.Contains("Character Map", line);
    }

    [Fact]
    public void Utterance_falls_back_when_app_name_unknown()
    {
        var line = TtsSignal.Utterance(null);
        Assert.False(string.IsNullOrWhiteSpace(line));
        Assert.DoesNotContain("null", line);
    }
}
