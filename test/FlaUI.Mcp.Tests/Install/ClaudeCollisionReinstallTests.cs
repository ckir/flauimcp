using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

/// <summary>D1 layer (b): when the recorded copy is no longer installed, rebuild it from the recorded
/// marketplace rather than reporting a dead end. Fix (a) removes the only KNOWN evictor, so this path
/// exists for evictions not yet foreseen — a future registrar change, a `claude` CLI behaviour shift, or
/// the user's own tooling.</summary>
public class ClaudeCollisionReinstallTests
{
    private static string TempState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-state-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempClaudeConfig(string? registryJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-claude-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(dir, "plugins"));
        if (registryJson is not null) File.WriteAllText(KnownMarketplaces.PathIn(dir), registryJson);
        return dir;
    }

    private sealed class FakeCli
    {
        public string ListJson = "[]";
        public readonly Dictionary<string, int> CodeFor = new();   // "plugin <verb> <target>" -> exit code
        public readonly List<string[]> Calls = new();

        public RunResult Run(string file, string[] args, string? cwd)
        {
            if (args.Length >= 2 && args[0] == "plugin" && args[1] == "list")
                return new RunResult(0, ListJson);
            Calls.Add(args);
            var key = string.Join(" ", args.Take(3));
            return new RunResult(CodeFor.TryGetValue(key, out var c) ? c : 0, "");
        }
    }

    private static readonly MarketplaceSource Src = new("flaui-mcp", "github", "ckir/flauimcp");
    private static readonly DisabledEntry Recorded = new("flaui-mcp@flaui-mcp", "user", null, Src);

    private const string RegistryWithAlias = """
        { "flaui-mcp": { "source": { "source": "github", "repo": "ckir/flauimcp" } } }
        """;

    // THE HAPPY PATH FOR (b): the copy was evicted and the marketplace is gone too, so all three steps
    // run - re-add, reinstall, enable.
    [Fact]
    public void An_evicted_copy_is_re_added_reinstalled_and_enabled()
    {
        var cli = new FakeCli { ListJson = "[]" };          // the recorded copy is NOT installed
        var s = TempState();
        CollisionMarker.Record(s, new[] { Recorded });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig(null)).Restore();   // registry file absent => alias absent

        Assert.Equal(new[] { "plugin", "marketplace", "add", "ckir/flauimcp" }, cli.Calls[0]);
        Assert.Equal(new[] { "plugin", "install", "flaui-mcp@flaui-mcp", "--scope", "user" }, cli.Calls[1]);
        Assert.Equal(new[] { "plugin", "enable", "flaui-mcp@flaui-mcp", "--scope", "user" }, cli.Calls[2]);
        // RULE 2: the message must say WHICH steps ran. "reinstalled from X" and "restored" are
        // different facts and an operator debugging a lost plugin needs to know which happened.
        Assert.Contains("reinstalled", warning!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ckir/flauimcp", warning);
    }

    // The alias is present AND is what we recorded: skip the re-add, but still reinstall and enable.
    [Fact]
    public void A_matching_live_alias_skips_the_re_add()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { Recorded });

        new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig(RegistryWithAlias)).Restore();

        Assert.DoesNotContain(cli.Calls, c => c.Contains("marketplace"));
        Assert.Equal(new[] { "plugin", "install", "flaui-mcp@flaui-mcp", "--scope", "user" }, cli.Calls[0]);
    }

    // ⚠ THE BRANCH THAT PROTECTS THE USER FROM A SILENTLY WRONG INSTALL. "Present by alias" is not "is
    // the thing we recorded": the alias is a local name the user controls. If they repointed it, a blind
    // reinstall would install whatever now sits behind that name.
    [Fact]
    public void A_repointed_alias_refuses_to_install_and_does_not_touch_the_marketplace()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { Recorded });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig("""
                { "flaui-mcp": { "source": { "source": "github", "repo": "someone-else/fork" } } }
                """)).Restore();

        Assert.Empty(cli.Calls);                               // no add, no install, no enable
        Assert.Contains("someone-else/fork", warning!);        // BOTH sources named
        Assert.Contains("ckir/flauimcp", warning);
    }

    // ⚠ ONE ENTRY'S PROBLEM IS NEVER ANOTHER ENTRY'S. Reading "stop" as `return` inside Restore's foreach
    // would strand every LATER entry because one alias mismatched.
    [Fact]
    public void A_repointed_alias_skips_only_that_entry()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[]
        {
            Recorded,
            new DisabledEntry("other@mkt", "user", null, new MarketplaceSource("mkt", "github", "a/b")),
        });

        new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig("""
                { "flaui-mcp": { "source": { "source": "github", "repo": "someone-else/fork" } },
                  "mkt":       { "source": { "source": "github", "repo": "a/b" } } }
                """)).Restore();

        // The SECOND entry was still restored despite the first entry's mismatch.
        Assert.Contains(cli.Calls, c => c.SequenceEqual(new[] { "plugin", "install", "other@mkt", "--scope", "user" }));
    }

    // THE FALSE-NEGATIVE DIRECTION for a `directory` source: a path differing only in casing or a
    // trailing separator is the SAME marketplace, and treating it as different would ABORT a restore
    // that should proceed.
    [Fact]
    public void A_directory_alias_differing_only_in_casing_is_the_same_marketplace()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[]
        {
            new DisabledEntry("acme@acme", "user", null,
                new MarketplaceSource("acme", "directory", @"C:\Marketplaces\acme")),
        });

        new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig("""
                { "acme": { "source": { "source": "directory", "path": "c:\\marketplaces\\acme\\" } } }
                """)).Restore();

        Assert.DoesNotContain(cli.Calls, c => c.Contains("marketplace"));   // matched: no re-add
        Assert.Contains(cli.Calls, c => c.Contains("install"));             // and it PROCEEDED
    }

    // No source recorded => a dead end, and the message must READ as one. Never guess a source.
    [Fact]
    public void An_evicted_copy_with_no_recorded_source_is_an_honest_dead_end()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig(RegistryWithAlias)).Restore();

        Assert.Empty(cli.Calls);
        Assert.Contains("no marketplace source was recorded", warning!);
    }

    // RULE 3: a partial restore reports as a FAILURE with the remaining commands, never as a success.
    [Fact]
    public void A_re_added_marketplace_with_a_failed_reinstall_reports_a_failure()
    {
        var cli = new FakeCli { ListJson = "[]" };
        cli.CodeFor["plugin install flaui-mcp@flaui-mcp"] = 1;
        var s = TempState();
        CollisionMarker.Record(s, new[] { Recorded });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig(null)).Restore();

        Assert.Contains("could NOT reinstall", warning!);
        Assert.Contains("claude plugin install flaui-mcp@flaui-mcp --scope user", warning);
        Assert.DoesNotContain(cli.Calls, c => c.Contains("enable"));   // never enable a phantom
    }

    // ⚠⚠ THE UNINSTALL-SAFETY TEST (in-process half). Malformed registry JSON must degrade to a warning,
    // never an exception - Inno runs uninstall waituntilterminated. MUTANT: neuter the catch in
    // KnownMarketplaces.Read and this goes RED.
    [Fact]
    public void Malformed_registry_json_degrades_to_a_warning_and_never_throws()
    {
        var cli = new FakeCli { ListJson = "[]" };
        var s = TempState();
        CollisionMarker.Record(s, new[] { Recorded });

        var warning = new ClaudeCollisionRemedy(cli.Run, s, _ => true,
            claudeConfigDir: TempClaudeConfig("{ not json at all")).Restore();

        Assert.NotNull(warning);
        Assert.Contains("could not be read", warning!);
        Assert.Contains(KnownMarketplaces.FileName, warning);
        Assert.Empty(cli.Calls);          // an UNKNOWN live source must not be overwritten blind
    }
}
