using System.IO;
using Xunit;

public class RepoPathsTests
{
    [Fact]
    public void Resolves_repo_root_containing_the_solution()
        => Assert.True(File.Exists(Path.Combine(RepoPaths.Root, "FlaUI.Mcp.slnx")));

    [Fact]
    public void Resolves_the_build_input_skill()
        => Assert.True(File.Exists(RepoPaths.At(".claude", "skills", "driving-flaui-mcp", "SKILL.md")));

    [Fact]
    public void The_two_tracked_copies_of_the_nudge_script_are_byte_identical()
    {
        // .claude/hooks/ is the LIVE copy: registered by .claude/settings.json, proven to fire, and
        // the one csproj EMBEDS (Task 3). plugins/flaui-mcp/scripts/ is a twin that nothing consumes.
        // Identical today with nothing enforcing it — this test stops the twin rotting unnoticed, and
        // is the one signal that would catch someone "fixing" the twin and wondering why it never ships.
        Assert.Equal(
            File.ReadAllBytes(RepoPaths.At(".claude", "hooks", "flaui-curate-nudge.sh")),
            File.ReadAllBytes(RepoPaths.At("plugins", "flaui-mcp", "scripts", "flaui-curate-nudge.sh")));
    }
}
