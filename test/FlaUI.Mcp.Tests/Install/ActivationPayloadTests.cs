using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class ActivationPayloadTests
{
    [Fact]
    public void Emits_well_formed_json_with_the_SessionStart_event_name()
    {
        using var doc = JsonDocument.Parse(ActivationPayload.ToJson());
        var hook = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", hook.GetProperty("hookEventName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(hook.GetProperty("additionalContext").GetString()));
    }

    /// The budget guards against SEMANTIC CREEP — the payload growing into a second copy of the skill.
    /// It measures PROSE ONLY, excluding the ToolSearch load line, because that line is mechanical API
    /// verbosity (5 tools x 2 registration prefixes) expressing exactly ONE concept. A total-character
    /// budget would force cutting a safety rule to pay for a tool name. The load line cannot itself
    /// bloat: Names_only_allow_listed_tools caps it at the five allow-listed tools. The line count is
    /// the primary anti-creep instrument.
    [Fact]
    public void Prose_stays_within_the_injected_text_budget()
    {
        var lines = ActivationPayload.Text.Split('\n');
        var prose = string.Join("\n", lines.Where(l => !l.StartsWith("ToolSearch ", StringComparison.Ordinal)));

        Assert.True(prose.Length <= 1100, $"payload prose is {prose.Length} chars (budget 1100)");
        Assert.True(lines.Length <= 15, $"payload is {lines.Length} lines (budget 15)");
    }

    [Fact]
    public void Names_only_allow_listed_tools()
    {
        var allowed = new[]
        {
            "desktop_list_windows", "desktop_open_window", "desktop_snapshot",
            "desktop_get_text", "desktop_input_status",
        };
        foreach (var token in ActivationPayload.Text.Split(new[] { ' ', ',', '"', '(', ')', '\n', '`' },
                                                           StringSplitOptions.RemoveEmptyEntries)
                                                    .Where(t => t.Contains("desktop_")))
        {
            // A token is either fully qualified (mcp__…__desktop_x) or BARE (desktop_x). LastIndexOf
            // returns -1 for the bare form, and a naive (-1 + 2) slice would silently strip the leading
            // 'd' and fail against the allow-list. Guard the no-prefix case explicitly.
            var cut = token.LastIndexOf("__", StringComparison.Ordinal);
            var bare = (cut >= 0 ? token[(cut + 2)..] : token).TrimEnd(':', '.', ';');
            Assert.Contains(bare, allowed);
        }
    }

    [Fact]
    public void Tells_the_agent_not_to_delegate_observation_to_the_human()
        => Assert.Contains("Never ask the user", ActivationPayload.Text, StringComparison.Ordinal);

    [Fact]
    public void The_verb_name_is_stable()
        => Assert.Equal("activation-payload", ActivationPayload.Verb);

    [Fact]
    public void The_router_recognises_the_verb_as_an_installer_verb()
        => Assert.True(CliRouter.IsInstallerVerb(new[] { ActivationPayload.Verb }),
            "verb missing from CliRouter.Verbs — Program.cs would start the MCP server instead of printing the payload");

    [Fact]
    public void The_router_prints_the_payload_json_and_exits_zero()
    {
        var outp = new StringWriter();
        var code = CliRouter.Run(new[] { ActivationPayload.Verb }, @"C:\fake\flaui-mcp.exe", outp);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(outp.ToString().Trim());
        Assert.Equal("SessionStart",
            doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
    }
}
