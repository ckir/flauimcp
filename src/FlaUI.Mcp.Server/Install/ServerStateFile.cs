using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlaUI.Mcp.Server.Install;

public enum ServerLiveness { Live, Dead, Unknown }

public sealed record ServerInstance(int StateVersion, int Pid, string? RulesPath, string? RulesSha256,
                                    ServerLiveness Liveness, string FilePath);

internal sealed record StateDto(
    [property: JsonPropertyName("stateVersion")] int StateVersion,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("processStartTimeUtc")] DateTime ProcessStartTimeUtc,
    [property: JsonPropertyName("redactionRulesPath")] string? RedactionRulesPath,
    [property: JsonPropertyName("redactionRulesSha256")] string? RedactionRulesSha256);

/// <summary>Per-INSTANCE boot state, so the check-redaction-rules CLI can tell whether a running
/// server is enforcing the rule file on disk. One file per pid — a single well-known path breaks the
/// moment two servers run, because the last to boot silently overwrites the first.</summary>
public static class ServerStateFile
{
    public const int CurrentStateVersion = 1;

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flaui-mcp");

    public static string DefaultDirectory => Path.Combine(Root, "instances");

    /// <summary>Durable, human-readable record of a startup refusal. ⚠ There is no existing
    /// `InstallPaths` type and no diagnostic-log helper in this repo — `DataDir`/`StateDir` live in a
    /// PRIVATE record inside CliRouter and are not reachable from Program.cs. So this writes its own
    /// file. Never throws: failing to log must not change the exit path.</summary>
    public static void TryLogStartupError(string message)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.AppendAllText(Path.Combine(Root, "startup-errors.log"),
                $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Write(string dir, int pid, DateTime startUtc, string? rulesPath, string? sha)
    {
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new StateDto(CurrentStateVersion, pid, startUtc, rulesPath, sha));
        var final = Path.Combine(dir, $"{pid}.json");
        var tmp = final + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, final, overwrite: true);   // atomic replace
    }

    public static void Delete(string dir, int pid)
    {
        try { File.Delete(Path.Combine(dir, $"{pid}.json")); } catch { }
    }

    public static IReadOnlyList<ServerInstance> ReadAll(string dir)
    {
        var result = new List<ServerInstance>();
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            StateDto? dto;
            try { dto = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(file)); }
            catch { continue; }                              // unreadable/garbage: not an instance
            if (dto is null) continue;

            var liveness = dto.StateVersion != CurrentStateVersion
                ? ServerLiveness.Unknown                     // version skew is UNKNOWN, never dead
                : Liveness(dto);
            result.Add(new ServerInstance(dto.StateVersion, dto.Pid, dto.RedactionRulesPath,
                                          dto.RedactionRulesSha256, liveness, file));
        }
        return result;
    }

    // ACCEPTANCE CONSTRAINT (b). Three outcomes, and "cannot determine" is a REAL one: reading another
    // process's StartTime needs PROCESS_QUERY_INFORMATION, and this server commonly runs elevated, so a
    // non-elevated CLI gets Access Denied on a perfectly healthy server.
    private static ServerLiveness Liveness(StateDto dto)
    {
        Process p;
        try { p = Process.GetProcessById(dto.Pid); }
        catch (ArgumentException) { return ServerLiveness.Dead; }   // positively gone
        catch { return ServerLiveness.Unknown; }

        try
        {
            var start = p.StartTime.ToUniversalTime();
            return Math.Abs((start - dto.ProcessStartTimeUtc).TotalSeconds) < 2
                ? ServerLiveness.Live
                : ServerLiveness.Dead;                              // PID recycled onto another process
        }
        catch { return ServerLiveness.Unknown; }                    // access denied — do NOT prune
    }

    /// <summary>Delete ONLY instances positively proven dead. Never deletes an Unknown: a diagnostic
    /// that destroys the state it is diagnosing would degrade a healthy elevated server into a
    /// permanent "no server running".</summary>
    public static void PruneDead(string dir)
    {
        foreach (var i in ReadAll(dir))
            if (i.Liveness == ServerLiveness.Dead)
                try { File.Delete(i.FilePath); } catch { }
    }
}
