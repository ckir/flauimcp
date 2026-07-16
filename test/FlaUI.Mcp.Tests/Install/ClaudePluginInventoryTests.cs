using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudePluginInventoryTests
{
    // Shape copied from live output (2026-07-16). user-scope rows carry no projectPath.
    private const string Live = """
    [
      { "id": "agy-autotrain@clavity-agy-autotrain", "version": "0.1.5", "scope": "user", "enabled": true,
        "installPath": "C:\\Users\\u\\.claude\\plugins\\cache\\x", "installedAt": "2026-07-13T15:26:49.371Z" },
      { "id": "csharp-lsp@claude-plugins-official", "version": "1.0.0", "scope": "local", "enabled": true,
        "projectPath": "C:\\Users\\u\\Development\\Rust\\clavity", "installPath": "C:\\c" },
      { "id": "andrej-karpathy-skills@karpathy-skills", "version": "1.0.0", "scope": "user", "enabled": false,
        "installPath": "C:\\k" }
    ]
    """;

    [Fact]
    public void Parses_id_scope_enabled_and_projectPath()
    {
        var e = ClaudePluginInventory.Parse(Live);

        Assert.Equal(3, e.Count);
        Assert.Equal("agy-autotrain@clavity-agy-autotrain", e[0].Id);
        Assert.Equal("user", e[0].Scope);
        Assert.True(e[0].Enabled);
        Assert.Null(e[0].ProjectPath);                                        // user scope has none
        Assert.Equal("local", e[1].Scope);
        Assert.Equal(@"C:\Users\u\Development\Rust\clavity", e[1].ProjectPath);
        Assert.False(e[2].Enabled);
    }

    // The real driver for the whole multi-entry contract: one id, many rows, each its own project.
    [Fact]
    public void Keeps_every_row_of_a_repeated_id_with_its_own_scope_and_project()
    {
        const string json = """
        [
          { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": true,  "projectPath": "C:\\a" },
          { "id": "flaui-mcp@flaui-mcp", "scope": "local", "enabled": false, "projectPath": "C:\\b" },
          { "id": "flaui-mcp@flaui-mcp", "scope": "user",  "enabled": true }
        ]
        """;

        var e = ClaudePluginInventory.Parse(json);

        Assert.Equal(3, e.Count);
        Assert.Equal(new[] { @"C:\a", @"C:\b", null }, e.Select(x => x.ProjectPath).ToArray());
        Assert.Equal(new[] { true, false, true }, e.Select(x => x.Enabled).ToArray());
    }

    [Fact]
    public void An_empty_array_yields_no_entries()
        => Assert.Empty(ClaudePluginInventory.Parse("[]"));

    // Every degenerate input must yield "I know nothing", never an exception: this runs inside a
    // hidden installer, where a throw becomes a mystery failure.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"not\": \"an array\" }")]
    [InlineData("[ 1, 2, 3 ]")]
    [InlineData("null")]
    [InlineData("[ { } ]")]
    [InlineData("[ { \"scope\": \"user\", \"enabled\": true } ]")]
    public void Degenerate_input_yields_no_entries_and_never_throws(string json)
        => Assert.Empty(ClaudePluginInventory.Parse(json));

    // An installed row with no explicit `enabled` is treated as ENABLED — the conservative reading:
    // it makes us look at it rather than silently skip a plugin that may be live.
    [Fact]
    public void A_row_without_enabled_is_treated_as_enabled()
    {
        var e = ClaudePluginInventory.Parse("""[ { "id": "x@y", "scope": "user" } ]""");
        Assert.True(Assert.Single(e).Enabled);
    }

    [Fact]
    public void A_row_without_scope_is_skipped_because_we_could_not_act_on_it()
        => Assert.Empty(ClaudePluginInventory.Parse("""[ { "id": "x@y", "enabled": true } ]"""));

    [Fact]
    public void Finds_all_rows_of_one_id()
    {
        var e = ClaudePluginInventory.Parse(Live);
        Assert.Single(ClaudePluginInventory.Matching(e, "csharp-lsp@claude-plugins-official"));
        Assert.Empty(ClaudePluginInventory.Matching(e, "flaui-mcp@flaui-mcp"));
    }
}
