using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class KnownMarketplacesTests
{
    private static string TempConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-claude-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(dir, "plugins"));
        return dir;
    }

    private static void Write(string cfg, string json) =>
        File.WriteAllText(KnownMarketplaces.PathIn(cfg), json);

    // ABSENT and UNREADABLE are DIFFERENT ANSWERS with OPPOSITE actions. Absent means there are no
    // registered marketplaces at all, so an alias is definitively absent and re-adding is both safe and
    // necessary; unreadable means the live source is genuinely unknown and re-adding could overwrite a
    // marketplace the user has repointed. Collapsing them dead-ends the restore in exactly the case
    // where it should proceed.
    [Fact]
    public void A_missing_file_is_FileAbsent_not_Unreadable()
    {
        var snap = KnownMarketplaces.Read(TempConfig());
        Assert.Equal(MarketplacesState.FileAbsent, snap.State);
        Assert.Empty(snap.ByName);
    }

    [Fact]
    public void A_null_config_dir_is_FileAbsent()
        => Assert.Equal(MarketplacesState.FileAbsent, KnownMarketplaces.Read(null).State);

    [Fact]
    public void Malformed_json_is_Unreadable_and_never_throws()
    {
        var cfg = TempConfig();
        Write(cfg, "{ this is not json");
        var snap = KnownMarketplaces.Read(cfg);
        Assert.Equal(MarketplacesState.Unreadable, snap.State);
        Assert.Empty(snap.ByName);
    }

    [Fact]
    public void All_three_kinds_are_read_from_their_own_field()
    {
        var cfg = TempConfig();
        Write(cfg, """
            { "flaui-mcp":  { "source": { "source": "github",    "repo": "ckir/flauimcp" } },
              "ecc":        { "source": { "source": "git",       "url":  "https://github.com/a/b.git" } },
              "clavity":    { "source": { "source": "directory", "path": "C:\\Programs\\clavity" } } }
            """);

        var snap = KnownMarketplaces.Read(cfg);

        Assert.Equal(MarketplacesState.Read, snap.State);
        Assert.Equal("ckir/flauimcp", snap.ByName["flaui-mcp"].Source);
        Assert.Equal("github", snap.ByName["flaui-mcp"].Kind);
        Assert.Equal("https://github.com/a/b.git", snap.ByName["ecc"].Source);
        Assert.Equal(@"C:\Programs\clavity", snap.ByName["clavity"].Source);
        Assert.Empty(snap.UnsupportedKinds);
    }

    // A kind this code does not recognise is recorded as ABSENT, never guessed — but it must be VISIBLE,
    // or the next unsupported kind is invisible until a restore silently fails weeks later.
    [Fact]
    public void An_unrecognised_kind_is_omitted_and_surfaced()
    {
        var cfg = TempConfig();
        Write(cfg, """{ "weird": { "source": { "source": "mercurial", "repo": "x" } } }""");

        var snap = KnownMarketplaces.Read(cfg);

        Assert.Equal(MarketplacesState.Read, snap.State);
        Assert.Empty(snap.ByName);
        Assert.Equal("mercurial", Assert.Single(snap.UnsupportedKinds));
    }

    // THE FALSE-NEGATIVE DIRECTION, which is the dangerous one: an unequal compare triggers the
    // "source DIFFERS" branch, which ABORTS a restore that should have proceeded.
    [Theory]
    [InlineData(@"C:\Marketplaces\acme", @"c:\marketplaces\acme")]      // casing
    [InlineData(@"C:\Marketplaces\acme", @"C:/Marketplaces/acme")]      // separator
    [InlineData(@"C:\Marketplaces\acme", @"C:\Marketplaces\acme\")]     // trailing separator
    [InlineData(@"C:\Marketplaces\acme", @"C:\Marketplaces\.\acme")]    // redundant segment
    public void Directory_sources_that_denote_the_same_place_compare_equal(string a, string b)
        => Assert.True(KnownMarketplaces.SameSource(
            new MarketplaceSource("m", "directory", a), new MarketplaceSource("m", "directory", b)));

    [Fact]
    public void Genuinely_different_directory_sources_compare_unequal()
        => Assert.False(KnownMarketplaces.SameSource(
            new MarketplaceSource("m", "directory", @"C:\Marketplaces\acme"),
            new MarketplaceSource("m", "directory", @"C:\Marketplaces\other")));

    // github/git are IDENTIFIERS, not paths — they compare ordinally (case-insensitively), and must NOT
    // be run through path normalisation, which would mangle a URL's forward slashes.
    [Fact]
    public void Git_and_github_sources_compare_without_path_normalisation()
    {
        Assert.True(KnownMarketplaces.SameSource(
            new MarketplaceSource("m", "github", "ckir/flauimcp"),
            new MarketplaceSource("m", "github", "CKIR/FlauiMcp")));
        Assert.False(KnownMarketplaces.SameSource(
            new MarketplaceSource("m", "git", "https://github.com/a/b.git"),
            new MarketplaceSource("m", "git", "https://github.com/a/c.git")));
    }

    [Fact]
    public void A_different_kind_is_never_the_same_source()
        => Assert.False(KnownMarketplaces.SameSource(
            new MarketplaceSource("m", "github", "a/b"), new MarketplaceSource("m", "directory", "a/b")));
}
