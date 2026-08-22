namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Writes the driving skill into Claude Code's config dir as a `skills-dir` plugin, which Claude
/// auto-loads as `flaui-mcp@skills-dir` at user scope.
///
/// This is NOT the agy deployer with a different path: AgyConfigWriter writes an agy-shaped bare
/// plugin.json at the plugin root, whereas Claude Code needs `.claude-plugin/plugin.json`. The
/// EMBEDDED RESOURCE is shared (a driving-only payload needs no second copy); the manifest is not.
///
/// The manifest is GENERATED rather than embedded because its version must track the assembly at
/// runtime — mirroring what AgyConfigWriter already does for the peer.
///
/// Never throws: the skill rides along with the registration, and a failure to place it must not
/// deny the user a working server.
/// </summary>
public sealed class ClaudeSkillDeployer
{
    private const string PluginName = "flaui-mcp";
    private readonly string _claudeConfigDir;

    public ClaudeSkillDeployer(string claudeConfigDir) => _claudeConfigDir = claudeConfigDir;

    /// <summary>`<claude-config>/skills/flaui-mcp` — the plugin root we own end to end.</summary>
    public string SkillRoot => Path.Combine(_claudeConfigDir, "skills", PluginName);

    /// <summary>
    /// Remove the deployed skill. The skill is a PRODUCT ARTIFACT, not user data — a version-locked
    /// manual for tools that are going away — so it goes on a plain uninstall, and must: leaving it
    /// would tell the agent to call desktop_* tools that no longer exist.
    /// Recursive is safe: SkillRoot is our own namespace and holds only what Deploy() wrote.
    /// </summary>
    public string? Remove()
    {
        try
        {
            if (Directory.Exists(SkillRoot)) Directory.Delete(SkillRoot, recursive: true);
            return null;
        }
        catch (Exception e)
        {
            return $"driving skill left behind at {SkillRoot}: {e.Message}";
        }
    }
}
