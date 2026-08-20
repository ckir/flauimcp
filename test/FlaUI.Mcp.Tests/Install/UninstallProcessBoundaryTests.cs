using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

/// <summary>THE UNINSTALL-MUST-ALWAYS-FINISH PIN, at the boundary that actually matters.
///
/// Inno runs `flaui-mcp.exe uninstall` as a PROCESS with `Flags: runhidden waituntilterminated`
/// (installer/flaui-mcp.iss:44-47), so what the user experiences is the OS EXIT CODE, not whether
/// Restore() returned. A user must always be able to remove the software.
///
/// ⚠ HONEST SCOPE, so nobody records this as proving more than it does: CliRouter.Isolate
/// (CliRouter.cs:387-391) already converts ANY throw into a Failed result, and Report deliberately never
/// exits non-zero for a partial failure. So this test passes with or without the never-throws handling
/// in KnownMarketplaces. It pins the END-TO-END boundary and would catch a future change that let a
/// failure reach the exit code; the proof that the new read degrades gracefully is the in-process mutant
/// on Malformed_registry_json_degrades_to_a_warning_and_never_throws.
///
/// Spawns a process; Category=Desktop, matching StdioProtocolCleanlinessTests.</summary>
[Trait("Category", "Desktop")]
public class UninstallProcessBoundaryTests
{
    [Fact]
    public void Uninstall_exits_zero_even_with_a_malformed_marketplace_registry()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "flaui-uninstall-" + Path.GetRandomFileName());
        var claude = Path.Combine(sandbox, "claude");
        var state = Path.Combine(sandbox, "state");
        Directory.CreateDirectory(Path.Combine(claude, "plugins"));
        Directory.CreateDirectory(state);

        try
        {
            // DELIBERATELY MALFORMED, at the exact path the restore reads.
            File.WriteAllText(Path.Combine(claude, "plugins", "known_marketplaces.json"), "{ not json at all");

            // A v2 marker recording a copy that the (faked) inventory will NOT list, so Restore takes the
            // reinstall branch and therefore READS the malformed file. Without this the test would exit
            // 0 without ever reaching the code under test.
            File.WriteAllText(Path.Combine(state, "disabled-plugins.json"), """
                { "version": 2,
                  "disabled": [ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "projectPath": null,
                                  "marketplace": { "name": "flaui-mcp", "kind": "github", "source": "ckir/flauimcp" } } ] }
                """);

            var psi = new ProcessStartInfo(LocateExe())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("uninstall");
            psi.ArgumentList.Add("--agent");
            psi.ArgumentList.Add("claude");
            psi.Environment["CLAUDE_CONFIG_DIR"] = claude;
            psi.Environment["FLAUI_MCP_CLAUDE_CONFIG_DIR"] = claude;
            psi.Environment["FLAUI_MCP_STATE_DIR"] = state;
            psi.Environment["FLAUI_MCP_DATA_DIR"] = Path.Combine(sandbox, "data");
            psi.Environment["FLAUI_MCP_AGY_PLUGINS_DIR"] = Path.Combine(sandbox, "agy");
            psi.Environment["FLAUI_MCP_STAGING_DIR"] = Path.Combine(sandbox, "staging");
            // Fake a claude that lists ONE unrelated plugin: the recorded id is therefore "no longer
            // installed" and Restore enters the reinstall branch.
            psi.Environment["FLAUI_MCP_FAKE_CLAUDE_COLLISION"] = "something-else@elsewhere";

            using var p = Process.Start(psi) ?? throw new Xunit.Sdk.XunitException("could not start the exe");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(60_000), "uninstall did not finish within 60s — a hang traps the user");

            Assert.True(p.ExitCode == 0,
                "uninstall must ALWAYS exit 0 — Inno runs it waituntilterminated and a non-zero exit " +
                $"traps the user with software they cannot remove. Exit {p.ExitCode}.\n" +
                $"stdout:\n{stdout}\nstderr:\n{stderr}");

            // NON-VACUITY: prove the run actually REACHED the malformed read. Without this the test
            // could pass having exited 0 for entirely unrelated reasons.
            Assert.Contains("known_marketplaces.json", stdout);
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    private static string LocateExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var config = baseDir.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlaUI.Mcp.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repo root (FlaUI.Mcp.slnx) from the test assembly.");
        var exe = Path.Combine(dir!.FullName, "src", "FlaUI.Mcp.Server", "bin", config,
            "net10.0-windows10.0.19041.0", "win-x64", "flaui-mcp.exe");
        Assert.True(File.Exists(exe), $"exe not found at {exe} — build FlaUI.Mcp.Server first.");
        return exe;
    }
}
