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

    /// The CORE is what a non-Claude client receives over `InitializeResult.instructions`. It must carry
    /// the behavioural rules and must NOT carry Claude Code's ToolSearch incantation, which means nothing
    /// to another client.
    [Fact]
    public void Core_carries_the_behavioural_rules_and_no_client_specific_mechanism()
    {
        Assert.Contains("Never ask the user", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.Contains("you can see and operate this Windows desktop yourself", ActivationPayload.Core,
            StringComparison.Ordinal);
        Assert.Contains("Triggers:", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.Contains("need a lease", ActivationPayload.Core, StringComparison.Ordinal);

        // Spec S6 names BOTH halves and says why: "assert it does NOT contain ToolSearch or
        // driving-flaui-mcp. This is the test that would have caught D1 being skipped." Omitting the
        // second half is exactly how the skill pointer leaks into a generic client's instructions.
        Assert.DoesNotContain("ToolSearch", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.DoesNotContain("driving-flaui-mcp", ActivationPayload.Core, StringComparison.Ordinal);
    }

    /// D2, the "addendum orphan" guard. If the Claude Code hook fails, an agent gets the CORE and never
    /// the ADDENDUM. Without a client-agnostic loading sentence it would be told it can drive the desktop,
    /// try a deferred tool, fail, and have no recovery instruction — worse than today's silence.
    [Fact]
    public void Core_tells_the_agent_tools_may_need_loading_without_naming_one_clients_mechanism()
    {
        Assert.Contains("may need loading", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolSearch", ActivationPayload.Core, StringComparison.Ordinal);
    }

    [Fact]
    public void Addendum_carries_the_claude_code_load_block()
    {
        Assert.Contains("ToolSearch \"select:", ActivationPayload.Addendum, StringComparison.Ordinal);
        Assert.Contains("Load the tools (one call):", ActivationPayload.Addendum, StringComparison.Ordinal);
        // D1: the skill pointer is client-specific and belongs HERE, not in the core.
        Assert.Contains("driving-flaui-mcp", ActivationPayload.Addendum, StringComparison.Ordinal);
    }

    /// Text is COMPOSED from the two halves, so they cannot drift apart.
    [Fact]
    public void Text_is_exactly_core_then_addendum()
        => Assert.Equal(ActivationPayload.Core + "\n" + ActivationPayload.Addendum, ActivationPayload.Text);

    /// The split REORDERS the payload (the load block moves to the end) but must not LOSE anything.
    ///
    /// ⚠ ONE original line is deliberately NOT asserted verbatim: the lease line carried BOTH a core
    /// concern (the lease boundary) and an addendum one (the skill pointer), so D1 forces it to be SPLIT
    /// across the two halves. Its two halves are asserted separately below. Every other line survives
    /// byte-for-byte.
    [Fact]
    public void The_split_preserves_every_original_payload_line()
    {
        var original = new[]
        {
            "flaui-mcp is installed: you can see and operate this Windows desktop yourself.",
            "Never ask the user to look at, read, or click inside a desktop app on your behalf, and do not infer UI state indirectly from process lists.",
            "Triggers: what is on screen; is an app running or responding; what a background terminal/console tab shows; clicking, typing or filling a GUI dialog; confirming a change landed in the real app.",
            "Load the tools (one call):",
            "If that returns no matches, retry ToolSearch \"desktop window snapshot\" and use ONLY: desktop_list_windows, desktop_open_window, desktop_snapshot, desktop_get_text, desktop_input_status. If one is absent, say so — never substitute a similar name.",
            "Read-only perception needs no lease and cannot disturb the user: desktop_list_windows(includeHandles:true) then desktop_snapshot wN then desktop_get_text wN eN.",
        };
        foreach (var line in original)
            Assert.Contains(line, ActivationPayload.Text, StringComparison.Ordinal);

        // The split lease line: both halves must still be there, on their respective sides.
        // ⚠ Pins the CORRECTED claim, not merely the words "need a lease". The original sentence said
        // the tab read "all need a lease", which is measurably FALSE (GuardWrite tests only
        // options.ReadOnly; InputGuard is not on that call path). A loosened assertion would have left
        // the corrected wording pinned by nothing, so both halves are asserted: the true lease boundary,
        // and the disturbance warning that stops the correction reading as "the tab read is free".
        Assert.Contains("Typing, clicking and dragging need a lease", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.Contains("reading a BACKGROUND terminal tab does not", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.Contains("but switches tabs", ActivationPayload.Core, StringComparison.Ordinal);
        Assert.Contains("driving-flaui-mcp skill", ActivationPayload.Addendum, StringComparison.Ordinal);
    }
}
