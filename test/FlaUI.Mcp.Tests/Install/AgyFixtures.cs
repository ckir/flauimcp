using System.IO;
using System.Text.Json.Nodes;
using FlaUI.Mcp.Server.Install;

/// <summary>Shared arrange helper for AgyConfigWriterTests and AgySkillDeployTests. In the global
/// namespace (not FlaUI.Mcp.Tests.Install) to match this directory's RepoPaths.cs precedent, so both
/// a namespaced test class and a global-namespace one can call it without an extra using.</summary>
internal static class AgyFixtures
{
    /// Writes exactly what Uninstall() looks for: the mcpServers entry, the permission token, and the
    /// plugin dir with a file in it. Replaces the old `w.Install(...)` arrange now that Install is gone.
    ///
    /// ⚠ INTERNAL, not private: it is called from AgyConfigWriterTests and AgySkillDeployTests, and a
    /// `private` member here fails to compile at those call sites with CS0122.
    internal static void SeedAgyInstall(string serversPath, string permsPath, string pluginsDir)
    {
        File.WriteAllText(serversPath,
            "{ \"mcpServers\": { \"flaui-mcp\": { \"command\": \"C:\\\\x\\\\flaui-mcp.exe\" } } }");

        var perms = JsoncFile.Load(permsPath);
        var permissions = perms["permissions"] as JsonObject;
        if (permissions is null) { permissions = new JsonObject(); perms["permissions"] = permissions; }
        var allow = permissions["allow"] as JsonArray;
        if (allow is null) { allow = new JsonArray(); permissions["allow"] = allow; }
        allow.Add("mcp(flaui-mcp/*)");
        JsoncFile.Save(permsPath, perms);

        var skillDir = Path.Combine(pluginsDir, "flaui-mcp", "skills", "driving-flaui-mcp");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: driving-flaui-mcp\n---\n");
    }
}
