# Installer Registration Rework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework the flaui-mcp installer to register as a unified plugin via the agent CLIs (`agy plugin install`, `claude plugin marketplace add/install`) for BOTH agents and STOP hand-writing any agent MCP config file, so it survives an Antigravity config-path migration.

**Architecture:** The install verb generates ONE plugin directory in an isolated staging path (`{exe-dir}\plugin\`) at runtime, then delegates registration to the agent CLIs. `CliRouter.Apply()` is rewired to a canonical sequence: generate artifacts → legacy cleanup (reusing the existing writers' `Uninstall()` as the migration sweep) → register via CLIs. The old `AgyConfigWriter.Install()` / `ClaudeCodeConfigWriter.Install()` write-paths are retired; their `Uninstall()` methods are reused as the sweep. CLI launches route through `cmd /C <cli> …` (to resolve `.cmd` npm shims) using the EXISTING `Func<string,string[],string?,RunResult>` seam.

**Tech Stack:** C# / .NET, xUnit. Source: `src/FlaUI.Mcp.Server/Install/`. Tests: `test/FlaUI.Mcp.Tests/Install/` (no `[Trait]` → run by default). Ships as flaui-mcp **v0.16.2**.

**Source spec:** `docs/superpowers/specs/2026-07-18-installer-registration-rework-design.md` (panel-GREEN at commit `1358585`). Read it before starting.

**Branch:** create `fix/installer-registration-rework` off `master`. No push/tag/merge without explicit user approval.

---

## Ground-truth facts (VERIFIED against the code at plan-time — do not re-derive)

- **The CLI seam is a delegate, NOT an interface:** `Func<string, string[], string?, RunResult>` = `(file, args, workingDirectory) => RunResult`. There is **no `ICliRunner`**. It is injected into `ClaudeCodeConfigWriter` (ctor, `ClaudeCodeConfigWriter.cs:15`) and `ClaudeCollisionRemedy` (ctor, `ClaudeCollisionRemedy.cs:41`), and produced by `CliRouter.ClaudeRunner` (`CliRouter.cs:324`).
- `RunResult` = `public readonly record struct RunResult(int Code, string Output)` (`ProcessRunner.cs:7`). Sentinels: `ProcessRunner.NotFound = -1` (`:18`), `ProcessRunner.TimedOut = -2` (`:20`), `ProcessRunner.DefaultTimeout = 30s` (`:23`). `Run(string file, string[] args, string? workingDirectory, TimeSpan timeout)` (`:25`) sets `UseShellExecute=false` and captures stdout/stderr via pipes (`:31,:33-34`).
- `AgentResult` (`AgentResult.cs:16`) = `record AgentResult(string Agent, AgentChange Change, string Detail, string? Warning = null)`. `AgentChange` (`AgentResult.cs:8`) = `{ Created, Updated, Unchanged, Removed, NotFound, Failed }`.
- `CliRouter.Apply(string agent, Paths paths, bool install, string exePath, IReadOnlyList<string>? extraArgs = null)` (`CliRouter.cs:241`); fans out agy/claude/generic each via `Isolate(string agent, Func<AgentResult> configure)` (`CliRouter.cs:360`). Install dispatch `CliRouter.cs:38-43`; uninstall `:103-120`.
- `Paths` is a **private nested `readonly record struct`** (`CliRouter.cs:208`), positional order `(AgyServers, AgyPerms, GenericPath, DataDir, AgyPluginsDir, ClaudeConfigDir, StateDir)`, built by `ResolvePaths(string? configOverride)` (`CliRouter.cs:212-239`). `exePath` is NOT in `Paths`; it is a separate `Apply` parameter.
- **The skill is deployed TWICE today:** agy's copy by `AgyConfigWriter` (`DeploySkill`, `AgyConfigWriter.cs:36`, into `{AgyPluginsDir}/flaui-mcp`) and Claude's by `ClaudeSkillDeployer.Deploy()` (`ClaudeSkillDeployer.cs:29`, into `{ClaudeConfigDir}/skills/flaui-mcp`). BOTH read the embedded resource **`FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md`**.
- `ClaudeCollisionRemedy.MarketplaceId = "flaui-mcp@flaui-mcp"` (`ClaudeCollisionRemedy.cs:32`). Our NEW marketplace id is `flaui-mcp@flaui-mcp-marketplace` (DIFFERENT), so the remedy will never disable our new plugin — the spec's "exclude-self" is satisfied for free by id distinctness. `Apply()` (`:51`) disables colliding `flaui-mcp@flaui-mcp` rows; `Restore()` (`:149`) re-enables from a `CollisionMarker` in the state dir.
- `ClaudeCodeConfigWriter.Install` runs `claude mcp remove --scope user flaui-mcp` then `claude mcp add --scope user flaui-mcp -- {exe} [args]`; `Uninstall()` runs `claude mcp remove --scope user flaui-mcp` (`ClaudeCodeConfigWriter.cs:24-27,:49`).
- **Test-fake patterns:** (a) unit — inject a `Func<string,string[],string?,RunResult>` into a ctor (e.g. `ClaudeCodeConfigWriterTests.cs:15`); (b) e2e — env seams `FLAUI_MCP_FAKE_CLAUDE_{MISSING,PRESENT,COLLISION}` consumed by `ClaudeRunner` (`CliRouter.cs:326-346`) + path-redirect env vars `FLAUI_MCP_{DATA_DIR,STATE_DIR,CLAUDE_CONFIG_DIR,AGY_PLUGINS_DIR}` (used in `CliRouterClaudeSkillTests.cs`). Test parallelization is disabled repo-wide (`xunit.runner.json`).

## Build & test commands (the repo's gate — do not invent stricter flags)

- Build: `dotnet build FlaUI.Mcp.sln -c Debug` → expect `Build succeeded. 0 Warning(s) 0 Error(s)`.
- Default test run (what CI gates on — excludes live-desktop tests): `dotnet test FlaUI.Mcp.sln --filter "Category!=Desktop&Category!=SyntheticInput"` → expect `Passed!  - Failed: 0`.
- Run one installer test file: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~ClassName"`.
- All new installer tests here are plain `[Fact]`/`[Theory]` with NO `[Trait]` (they run headlessly in the default gate; no real `claude`/`agy` needed — the seam is faked).

---

## File Structure

**New source files** (each under `src/FlaUI.Mcp.Server/Install/`):
- `CliResolver.cs` — resolve a CLI name (`agy`/`claude`) to a full path via PATH+PATHEXT + known fallbacks, or `null` (presence gate). Pure/injectable for tests.
- `CliInvoker.cs` — wraps (resolver + runner): resolves the CLI; if absent returns `RunResult(NotFound,"")`; else runs it via `cmd /C <cli> <args…>` through the `Func` seam. The single home of the `cmd /C` + discrete-ArgumentList orchestration.
- `PluginArtifactWriter.cs` — generates the staging plugin dir: `plugin.json`, `.mcp.json`, `.claude-plugin/marketplace.json` (all via `JsonSerializer`), and `skills/driving-flaui-mcp/SKILL.md` (from the embedded resource).
- `AgyPluginRegistrar.cs` — `agy plugin uninstall`(swallow)→`install "<stagingDir>"`; `Unregister()`.
- `ClaudePluginRegistrar.cs` — `claude plugin marketplace remove`(swallow)→`add`→`uninstall`(swallow)→`install`→read-back active-state; `Unregister()`.

**New test files** (under `test/FlaUI.Mcp.Tests/Install/`): `CliResolverTests.cs`, `CliInvokerTests.cs`, `PluginArtifactWriterTests.cs`, `AgyPluginRegistrarTests.cs`, `ClaudePluginRegistrarTests.cs`, plus additions to `CliRouterClaudeSkillTests.cs` (or a new `CliRouterPluginRegistrationTests.cs`).

**Modified:** `CliRouter.cs` (rewire `Apply()` install + uninstall to the canonical sequence; add staging-dir resolution + agy env seams; conditional staging delete + fail-open warning on uninstall). `AgyConfigWriter.cs` / `ClaudeCodeConfigWriter.cs` (`Install()` no longer called from `Apply`; `Uninstall()` reused as sweep — keep the classes). Docs: `docs/operator-manual.md`, `README.md`. Version bumps: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`, `installer/flaui-mcp.iss`, `plugins/flaui-mcp/.claude-plugin/plugin.json`.

**Cross-repo (bundled fix):** `C:\Users\user\Development\Rust\clavity\commonmemory\installer\commonmemory.iss:32` — add `.claude-plugin` to `Excludes`.

---

## Constants (define ONCE, reuse verbatim — panel-pinned invariants)

Add to a small internal static holder (e.g. top of `PluginArtifactWriter.cs`) and reference everywhere:

```csharp
internal static class PluginIds
{
    public const string PluginName      = "flaui-mcp";
    public const string MarketplaceName = "flaui-mcp-marketplace";
    public const string InstallTarget   = "flaui-mcp@flaui-mcp-marketplace"; // PluginName@MarketplaceName
    public const string SkillResource   = "FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md";
}
```

---

### Task 1: CliResolver — presence gate (PATH + PATHEXT + fallbacks)

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/CliResolver.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/CliResolverTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Server.Install;
using Xunit;

public class CliResolverTests
{
    // Fake PATH with two dirs; fileExists says only "C:\\tools\\agy.cmd" exists.
    private static string? ResolveWith(string cli, string path, params string[] existing)
    {
        var set = new HashSet<string>(existing, System.StringComparer.OrdinalIgnoreCase);
        return CliResolver.Resolve(cli, path, pathext: ".COM;.EXE;.BAT;.CMD", fileExists: set.Contains);
    }

    [Fact]
    public void Finds_cmd_shim_via_pathext()
    {
        var found = ResolveWith("agy", @"C:\other;C:\tools", @"C:\tools\agy.cmd");
        Assert.Equal(@"C:\tools\agy.cmd", found);
    }

    [Fact]
    public void Prefers_exe_over_cmd_by_pathext_order()
    {
        var found = ResolveWith("agy", @"C:\tools", @"C:\tools\agy.cmd", @"C:\tools\agy.exe");
        Assert.Equal(@"C:\tools\agy.exe", found); // .EXE precedes .CMD in PATHEXT
    }

    [Fact]
    public void Returns_null_when_absent()
    {
        Assert.Null(ResolveWith("claude", @"C:\tools", @"C:\tools\agy.cmd"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~CliResolverTests"`
Expected: FAIL — `CliResolver` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

```csharp
using System;
using System.IO;

namespace FlaUI.Mcp.Server.Install;

/// Resolves a bare CLI name (agy/claude) to a full path using PATH + PATHEXT and known
/// npm/cargo/local fallback dirs, so registration can gate on presence (absent => skip+report,
/// never abort). Pure given its injected PATH/PATHEXT/fileExists — unit-tested headlessly.
public static class CliResolver
{
    public static string? Resolve(
        string cli,
        string? path = null,
        string? pathext = null,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        pathext ??= Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";

        var exts = pathext.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in EnumerateDirs(dirs))
        {
            // If cli already carries an extension, try it verbatim first.
            var verbatim = Path.Combine(dir, cli);
            if (Path.HasExtension(cli) && fileExists(verbatim)) return verbatim;
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, cli + ext);
                if (fileExists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateDirs(string[] pathDirs)
    {
        foreach (var d in pathDirs) yield return d;
        var appdata = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appdata)) yield return Path.Combine(appdata, "npm");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".cargo", "bin");
            yield return Path.Combine(home, ".local", "bin");
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~CliResolverTests"`
Expected: `Passed!  - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CliResolver.cs test/FlaUI.Mcp.Tests/Install/CliResolverTests.cs
git commit -m "feat(install): CliResolver — presence gate via PATH+PATHEXT+fallbacks"
```

---

### Task 2: CliInvoker — cmd /C orchestration + NotFound gate

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/CliInvoker.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/CliInvokerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class CliInvokerTests
{
    [Fact]
    public void Present_cli_routed_through_cmd_slashC_with_discrete_args()
    {
        var calls = new List<(string file, string[] args)>();
        var invoker = new CliInvoker(
            run: (file, args, cwd) => { calls.Add((file, args)); return new RunResult(0, "ok"); },
            resolve: _ => @"C:\tools\agy.cmd"); // present

        var r = invoker.Invoke("agy", "plugin", "install", @"C:\App With Space\plugin");

        Assert.Equal(0, r.Code);
        var (file, args) = Assert.Single(calls);
        Assert.Equal("cmd.exe", file);
        // Discrete elements — never one concatenated string; path stays its own element (quoted by .NET).
        Assert.Equal(new[] { "/C", "agy", "plugin", "install", @"C:\App With Space\plugin" }, args);
    }

    [Fact]
    public void Absent_cli_returns_NotFound_without_running()
    {
        var ran = false;
        var invoker = new CliInvoker(
            run: (_, _, _) => { ran = true; return new RunResult(0, ""); },
            resolve: _ => null); // absent

        var r = invoker.Invoke("claude", "plugin", "list");

        Assert.False(ran);
        Assert.Equal(ProcessRunner.NotFound, r.Code);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~CliInvokerTests"`
Expected: FAIL — `CliInvoker` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System;
using System.Linq;

namespace FlaUI.Mcp.Server.Install;

/// The single home of the CLI launch orchestration. `agy`/`claude` are often npm .cmd shims that
/// CreateProcess cannot launch by bare name, so every call goes through `cmd /C <cli> <args…>`
/// (cmd resolves the shim on PATH). Args are passed as DISCRETE elements — never one concatenated
/// string (a single quoted blob would be treated as the exe name). ProcessRunner already captures
/// stdout/stderr via pipes, so NO `> tmp 2>&1` shell redirect is used. Absent CLI => NotFound.
public sealed class CliInvoker
{
    private readonly Func<string, string[], string?, RunResult> _run;
    private readonly Func<string, string?> _resolve;

    public CliInvoker(Func<string, string[], string?, RunResult> run, Func<string, string?> resolve)
    {
        _run = run;
        _resolve = resolve;
    }

    /// True when the CLI resolves on PATH (used to gate skip-vs-run at the caller).
    public bool IsPresent(string cli) => _resolve(cli) is not null;

    public RunResult Invoke(string cli, params string[] args)
    {
        if (_resolve(cli) is null) return new RunResult(ProcessRunner.NotFound, "");
        var full = new[] { "/C", cli }.Concat(args).ToArray();
        return _run("cmd.exe", full, null);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~CliInvokerTests"`
Expected: `Passed!  - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CliInvoker.cs test/FlaUI.Mcp.Tests/Install/CliInvokerTests.cs
git commit -m "feat(install): CliInvoker — cmd /C orchestration + NotFound gate"
```

---

### Task 3: PluginArtifactWriter — .mcp.json via JsonSerializer

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/PluginArtifactWriterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using System.Text.Json;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class PluginArtifactWriterTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "flaui-plugin-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Mcp_json_has_windows_path_escaped_and_correct_shape()
    {
        var staging = TempDir();
        var exe = @"C:\Users\me\AppData\Local\Programs\FlaUI.Mcp\flaui-mcp.exe";

        new PluginArtifactWriter(staging).WriteMcpJson(exe);

        var raw = File.ReadAllText(Path.Combine(staging, ".mcp.json"));
        Assert.Contains(@"C:\\Users\\me", raw); // backslashes JSON-escaped, not raw \U/\P

        using var doc = JsonDocument.Parse(raw); // must be valid JSON
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("flaui-mcp");
        Assert.Equal(exe, server.GetProperty("command").GetString());
        Assert.Equal(0, server.GetProperty("args").GetArrayLength());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~PluginArtifactWriterTests"`
Expected: FAIL — `PluginArtifactWriter` does not exist.

- [ ] **Step 3: Write the implementation** (this task adds the class + `WriteMcpJson`; Task 4 adds the rest)

```csharp
using System.IO;
using System.Text.Json;

namespace FlaUI.Mcp.Server.Install;

internal static class PluginIds
{
    public const string PluginName      = "flaui-mcp";
    public const string MarketplaceName = "flaui-mcp-marketplace";
    public const string InstallTarget   = "flaui-mcp@flaui-mcp-marketplace";
    public const string SkillResource   = "FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md";
}

/// Generates the unified plugin directory in the isolated staging dir at install time.
/// Everything is written via JsonSerializer (never string interpolation) so Windows backslash
/// paths are correctly escaped. The installer packages ONLY the exe (flaui-mcp.iss:25); these
/// artifacts do not exist on disk until this runs.
public sealed class PluginArtifactWriter
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly string _stagingDir;

    public PluginArtifactWriter(string stagingDir) => _stagingDir = stagingDir;

    public void WriteMcpJson(string exePath)
    {
        Directory.CreateDirectory(_stagingDir);
        var model = new
        {
            mcpServers = new System.Collections.Generic.Dictionary<string, object>
            {
                [PluginIds.PluginName] = new { command = exePath, args = System.Array.Empty<string>() }
            }
        };
        File.WriteAllText(Path.Combine(_stagingDir, ".mcp.json"), JsonSerializer.Serialize(model, Pretty));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~PluginArtifactWriterTests"`
Expected: `Passed!  - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs test/FlaUI.Mcp.Tests/Install/PluginArtifactWriterTests.cs
git commit -m "feat(install): PluginArtifactWriter.WriteMcpJson (JsonSerializer, escaped path)"
```

---

### Task 4: PluginArtifactWriter — marketplace.json, plugin.json, skill + Generate()

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/PluginArtifactWriterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Generate_writes_all_four_artifacts_with_flat_marketplace_source_and_skill()
    {
        var staging = TempDir();
        new PluginArtifactWriter(staging).Generate(@"C:\p\flaui-mcp.exe", version: "0.16.2");

        Assert.True(File.Exists(Path.Combine(staging, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(staging, "plugin.json")));

        var mkt = Path.Combine(staging, ".claude-plugin", "marketplace.json");
        Assert.True(File.Exists(mkt));
        using var doc = JsonDocument.Parse(File.ReadAllText(mkt));
        Assert.Equal("flaui-mcp-marketplace", doc.RootElement.GetProperty("name").GetString());
        var plugin = doc.RootElement.GetProperty("plugins")[0];
        Assert.Equal("flaui-mcp", plugin.GetProperty("name").GetString());
        Assert.Equal(".", plugin.GetProperty("source").GetString()); // FLAT, not ./plugins/<name>

        var skill = Path.Combine(staging, "skills", "driving-flaui-mcp", "SKILL.md");
        Assert.True(File.Exists(skill));
        Assert.Contains("Driving FlaUI.Mcp", File.ReadAllText(skill)); // real skill content from embedded resource
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~PluginArtifactWriterTests"`
Expected: FAIL — `Generate` not defined.

- [ ] **Step 3: Write the implementation** (add to `PluginArtifactWriter`)

```csharp
    public void Generate(string exePath, string version)
    {
        WriteMcpJson(exePath);
        WritePluginJson(version);
        WriteMarketplaceJson();
        WriteSkill();
    }

    private void WritePluginJson(string version)
    {
        var model = new { name = PluginIds.PluginName, version, description = "Drive the Windows desktop via MCP." };
        File.WriteAllText(Path.Combine(_stagingDir, "plugin.json"), JsonSerializer.Serialize(model, Pretty));
    }

    private void WriteMarketplaceJson()
    {
        var dir = Path.Combine(_stagingDir, ".claude-plugin");
        Directory.CreateDirectory(dir);
        var model = new
        {
            schema  = "https://code.claude.com/schemas/marketplace.json",
            name    = PluginIds.MarketplaceName,
            owner   = new { name = "ckir" },
            plugins = new[] { new { name = PluginIds.PluginName, source = ".", description = "Drive the Windows desktop via MCP." } }
        };
        // "$schema" is not a valid C# member name — serialize then fix the key, or use a JsonObject.
        var json = JsonSerializer.Serialize(model, Pretty).Replace("\"schema\":", "\"$schema\":");
        File.WriteAllText(Path.Combine(dir, "marketplace.json"), json);
    }

    private void WriteSkill()
    {
        var target = Path.Combine(_stagingDir, "skills", "driving-flaui-mcp");
        Directory.CreateDirectory(target);
        // Read the embedded skill exactly as AgyConfigWriter.DeploySkill (AgyConfigWriter.cs:36) does.
        using var stream = typeof(PluginArtifactWriter).Assembly.GetManifestResourceStream(PluginIds.SkillResource)
            ?? throw new FileNotFoundException($"embedded skill resource missing: {PluginIds.SkillResource}");
        using var reader = new StreamReader(stream);
        File.WriteAllText(Path.Combine(target, "SKILL.md"), reader.ReadToEnd());
    }
```

> NOTE: confirm the exact embedded-resource name against `AgyConfigWriter.cs:36` / `ClaudeSkillDeployer.cs:20` at implementation time (Step 0 state-check). If the driving skill's H1 differs from `"Driving FlaUI.Mcp"`, adjust the test's `Assert.Contains` to a stable substring actually present in the seed.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~PluginArtifactWriterTests"`
Expected: `Passed!  - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs test/FlaUI.Mcp.Tests/Install/PluginArtifactWriterTests.cs
git commit -m "feat(install): PluginArtifactWriter.Generate — marketplace(source='.')+plugin.json+skill"
```

---

### Task 5: AgyPluginRegistrar — register/unregister via CLI

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/AgyPluginRegistrar.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/AgyPluginRegistrarTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class AgyPluginRegistrarTests
{
    private static (AgyPluginRegistrar reg, List<string[]> calls) Present()
    {
        var calls = new List<string[]>();
        var invoker = new CliInvoker(
            run: (_, args, _) => { calls.Add(args); return new RunResult(0, ""); },
            resolve: _ => @"C:\tools\agy.cmd");
        return (new AgyPluginRegistrar(invoker), calls);
    }

    [Fact]
    public void Register_runs_idempotent_uninstall_then_install_with_staging_dir()
    {
        var (reg, calls) = Present();
        var r = reg.Register(@"C:\App\plugin");

        Assert.Equal(AgentChange.Created, r.Change);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "/C", "agy", "plugin", "uninstall", "flaui-mcp" }, calls[0]); // swallow
        Assert.Equal(new[] { "/C", "agy", "plugin", "install", @"C:\App\plugin" }, calls[1]);
    }

    [Fact]
    public void Register_skips_and_reports_NotFound_when_agy_absent()
    {
        var invoker = new CliInvoker(run: (_, _, _) => new RunResult(0, ""), resolve: _ => null);
        var r = new AgyPluginRegistrar(invoker).Register(@"C:\App\plugin");
        Assert.Equal(AgentChange.NotFound, r.Change);
    }

    [Fact]
    public void Register_fails_when_install_exits_nonzero()
    {
        var calls = new List<string[]>();
        var invoker = new CliInvoker(
            run: (_, args, _) => { calls.Add(args); return args[3] == "install" ? new RunResult(1, "boom") : new RunResult(0, ""); },
            resolve: _ => @"C:\tools\agy.cmd");
        var r = new AgyPluginRegistrar(invoker).Register(@"C:\App\plugin");
        Assert.Equal(AgentChange.Failed, r.Change);
        Assert.Contains("boom", r.Detail);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~AgyPluginRegistrarTests"`
Expected: FAIL — `AgyPluginRegistrar` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace FlaUI.Mcp.Server.Install;

/// Registers flaui-mcp with agy via `agy plugin install "<stagingDir>"` (agy copies the dir into its
/// managed plugins dir itself). Idempotent: uninstall-then-install. Absent agy => NotFound (skip+report).
public sealed class AgyPluginRegistrar
{
    private const string Agy = "agy";
    private readonly CliInvoker _cli;

    public AgyPluginRegistrar(CliInvoker cli) => _cli = cli;

    public AgentResult Register(string stagingDir)
    {
        if (!_cli.IsPresent(Agy))
            return new AgentResult("agy", AgentChange.NotFound, "agy CLI not found — skipped");

        _cli.Invoke(Agy, "plugin", "uninstall", PluginIds.PluginName); // swallow (absent on fresh installs)
        var r = _cli.Invoke(Agy, "plugin", "install", stagingDir);
        return r.Code == 0
            ? new AgentResult("agy", AgentChange.Created, $"agy plugin install {PluginIds.PluginName}")
            : new AgentResult("agy", AgentChange.Failed, $"agy plugin install failed (exit {r.Code}): {r.Output}");
    }

    public AgentResult Unregister()
    {
        if (!_cli.IsPresent(Agy))
            return new AgentResult("agy", AgentChange.NotFound, "agy CLI not found — skipped");
        var r = _cli.Invoke(Agy, "plugin", "uninstall", PluginIds.PluginName);
        return r.Code == 0
            ? new AgentResult("agy", AgentChange.Removed, "agy plugin uninstall")
            : new AgentResult("agy", AgentChange.Failed, $"agy plugin uninstall failed (exit {r.Code}): {r.Output}");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~AgyPluginRegistrarTests"`
Expected: `Passed!  - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/AgyPluginRegistrar.cs test/FlaUI.Mcp.Tests/Install/AgyPluginRegistrarTests.cs
git commit -m "feat(install): AgyPluginRegistrar — agy plugin install/uninstall + NotFound skip"
```

---

### Task 6: ClaudePluginRegistrar — marketplace add/install + active-state read-back

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/ClaudePluginRegistrar.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudePluginRegistrarTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class ClaudePluginRegistrarTests
{
    // A fake that records argv and answers `plugin list` with a provided body.
    private static CliInvoker Fake(List<string[]> calls, string listBody, int listCode = 0)
        => new CliInvoker(
            run: (_, args, _) =>
            {
                calls.Add(args);
                var isList = System.Array.IndexOf(args, "list") >= 0;
                return isList ? new RunResult(listCode, listBody) : new RunResult(0, "");
            },
            resolve: _ => @"C:\tools\claude.cmd");

    [Fact]
    public void Register_runs_remove_add_uninstall_install_then_readback()
    {
        var calls = new List<string[]>();
        // read-back shows the plugin present AND enabled
        var reg = new ClaudePluginRegistrar(Fake(calls, "flaui-mcp@flaui-mcp-marketplace  enabled"));
        var r = reg.Register(@"C:\App\plugin");

        Assert.Equal(AgentChange.Created, r.Change);
        Assert.Equal(new[] { "/C", "claude", "plugin", "marketplace", "remove", "flaui-mcp-marketplace" }, calls[0]);
        Assert.Equal(new[] { "/C", "claude", "plugin", "marketplace", "add", @"C:\App\plugin", "--scope", "user" }, calls[1]);
        Assert.Equal(new[] { "/C", "claude", "plugin", "uninstall", "flaui-mcp" }, calls[2]);
        Assert.Equal(new[] { "/C", "claude", "plugin", "install", "flaui-mcp@flaui-mcp-marketplace", "--scope", "user" }, calls[3]);
        Assert.Contains(calls, c => System.Array.IndexOf(c, "list") >= 0); // read-back happened
    }

    [Fact]
    public void Register_fails_when_readback_shows_disabled()
    {
        var calls = new List<string[]>();
        var reg = new ClaudePluginRegistrar(Fake(calls, "flaui-mcp@flaui-mcp-marketplace  Disabled"));
        var r = reg.Register(@"C:\App\plugin");
        Assert.Equal(AgentChange.Failed, r.Change); // present-but-not-active is NOT success
    }

    [Fact]
    public void Register_fails_when_readback_absent()
    {
        var calls = new List<string[]>();
        var reg = new ClaudePluginRegistrar(Fake(calls, "some-other-plugin  enabled"));
        var r = reg.Register(@"C:\App\plugin");
        Assert.Equal(AgentChange.Failed, r.Change);
    }

    [Fact]
    public void Register_skips_when_claude_absent()
    {
        var reg = new ClaudePluginRegistrar(new CliInvoker((_, _, _) => new RunResult(0, ""), _ => null));
        Assert.Equal(AgentChange.NotFound, reg.Register(@"C:\App\plugin").Change);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~ClaudePluginRegistrarTests"`
Expected: FAIL — `ClaudePluginRegistrar` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System;

namespace FlaUI.Mcp.Server.Install;

/// Registers flaui-mcp with Claude Code as a local marketplace. Idempotent remove-then-add, then a
/// read-back that requires the plugin to be present AND active (a bare substring "present" is
/// false-GREEN if it listed but failed to load). Absent claude => NotFound (skip+report).
public sealed class ClaudePluginRegistrar
{
    private const string Claude = "claude";
    private readonly CliInvoker _cli;

    public ClaudePluginRegistrar(CliInvoker cli) => _cli = cli;

    public AgentResult Register(string stagingDir)
    {
        if (!_cli.IsPresent(Claude))
            return new AgentResult("claude", AgentChange.NotFound, "claude CLI not found — skipped");

        _cli.Invoke(Claude, "plugin", "marketplace", "remove", PluginIds.MarketplaceName);          // swallow
        var add = _cli.Invoke(Claude, "plugin", "marketplace", "add", stagingDir, "--scope", "user");
        if (add.Code != 0)
            return new AgentResult("claude", AgentChange.Failed, $"marketplace add failed (exit {add.Code}): {add.Output}");

        _cli.Invoke(Claude, "plugin", "uninstall", PluginIds.PluginName);                            // swallow
        var install = _cli.Invoke(Claude, "plugin", "install", PluginIds.InstallTarget, "--scope", "user");
        if (install.Code != 0)
            return new AgentResult("claude", AgentChange.Failed, $"plugin install failed (exit {install.Code}): {install.Output}");

        var list = _cli.Invoke(Claude, "plugin", "list");
        if (list.Code != 0 || !IsActive(list.Output))
            return new AgentResult("claude", AgentChange.Failed,
                $"read-back FAILED: {PluginIds.InstallTarget} not active after install: {list.Output}");

        return new AgentResult("claude", AgentChange.Created, $"installed {PluginIds.InstallTarget}");
    }

    public AgentResult Unregister()
    {
        if (!_cli.IsPresent(Claude))
            return new AgentResult("claude", AgentChange.NotFound, "claude CLI not found — skipped");
        _cli.Invoke(Claude, "plugin", "uninstall", PluginIds.PluginName);                            // swallow
        var mkt = _cli.Invoke(Claude, "plugin", "marketplace", "remove", PluginIds.MarketplaceName);
        return mkt.Code == 0
            ? new AgentResult("claude", AgentChange.Removed, "claude plugin + marketplace removed")
            : new AgentResult("claude", AgentChange.Failed, $"marketplace remove failed (exit {mkt.Code}): {mkt.Output}");
    }

    /// The plugin's line must be present AND not in a Disabled/Error state. If `plugin list` output
    /// surfaces no state token, presence alone is accepted (matches clavity's proven substring oracle).
    private static bool IsActive(string listOutput)
    {
        foreach (var line in listOutput.Split('\n'))
        {
            if (line.IndexOf(PluginIds.InstallTarget, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (line.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~ClaudePluginRegistrarTests"`
Expected: `Passed!  - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudePluginRegistrar.cs test/FlaUI.Mcp.Tests/Install/ClaudePluginRegistrarTests.cs
git commit -m "feat(install): ClaudePluginRegistrar — marketplace add/install + active-state read-back"
```

---

### Task 7: Wire the canonical INSTALL sequence into CliRouter.Apply

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs` (the agy + claude branches of `Apply`, `:246-297`; add staging-dir resolution + an `AgyRunner`/resolve seam)
- Test: `test/FlaUI.Mcp.Tests/Install/CliRouterPluginRegistrationTests.cs`

**Step 0 — state-check:** open `CliRouter.cs:241-305` and confirm the agy branch (`:246-251`) still constructs `AgyConfigWriter(...).Install(...)` and the claude branch (`:252-297`) still constructs `ClaudeCodeConfigWriter` + `ClaudeSkillDeployer` + `ClaudeCollisionRemedy`. If the structure differs, STOP and report `STATE_MISMATCH: <what>`.

- [ ] **Step 1: Write the failing test** (e2e through `CliRouter.Run`, faking BOTH agents present via env seams + redirected paths). Mirror the env-var setup in `CliRouterClaudeSkillTests.cs` (`:41` etc.).

```csharp
using System;
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

[Collection("cli-router-env")] // if such a collection exists; else rely on repo-wide no-parallel
public class CliRouterPluginRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flaui-cr-" + Path.GetRandomFileName());

    public CliRouterPluginRegistrationTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("FLAUI_MCP_DATA_DIR", Path.Combine(_root, "data"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_STATE_DIR", Path.Combine(_root, "state"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_CLAUDE_CONFIG_DIR", Path.Combine(_root, "claude"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_AGY_PLUGINS_DIR", Path.Combine(_root, "agy-plugins"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_STAGING_DIR", Path.Combine(_root, "plugin"));
        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_CLAUDE_PRESENT", "1");
        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_AGY_PRESENT", "1");
    }

    public void Dispose()
    {
        foreach (var v in new[] { "FLAUI_MCP_DATA_DIR","FLAUI_MCP_STATE_DIR","FLAUI_MCP_CLAUDE_CONFIG_DIR",
                                  "FLAUI_MCP_AGY_PLUGINS_DIR","FLAUI_MCP_STAGING_DIR",
                                  "FLAUI_MCP_FAKE_CLAUDE_PRESENT","FLAUI_MCP_FAKE_AGY_PRESENT" })
            Environment.SetEnvironmentVariable(v, null);
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Install_generates_staging_artifacts_and_writes_no_agent_config_file()
    {
        var exe = Path.Combine(_root, "flaui-mcp.exe");
        File.WriteAllText(exe, "");
        var sw = new StringWriter();

        var code = CliRouter.Run(new[] { "install", "--agent", "all" }, exe, sw);

        Assert.Equal(0, code);
        var staging = Path.Combine(_root, "plugin");
        Assert.True(File.Exists(Path.Combine(staging, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(staging, ".claude-plugin", "marketplace.json")));
        Assert.True(File.Exists(Path.Combine(staging, "skills", "driving-flaui-mcp", "SKILL.md")));
        // NO hand-written agy config: mcp_config.json / settings.json must not carry a flaui-mcp mcpServers block.
        var mcpConfig = Path.Combine(_root, "agy-cfg-unused");
        Assert.False(File.Exists(mcpConfig));
    }
}
```

> NOTE: match the exact env-collection / setup idiom already used in `CliRouterClaudeSkillTests.cs` (Step-0 read it). If that file resolves agy paths differently, mirror it precisely rather than inventing a new env name.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~CliRouterPluginRegistrationTests"`
Expected: FAIL — staging artifacts not generated (old path still writes config).

- [ ] **Step 3: Rewire the install branches of `Apply`.** Replace the agy branch (`CliRouter.cs:246-251`) and the claude install half (`:252-297`) so the install path becomes:

```csharp
// Near the top of Apply, once, derive the staging dir + a CliInvoker factory.
var stagingDir = Environment.GetEnvironmentVariable("FLAUI_MCP_STAGING_DIR")
                 ?? Path.Combine(Path.GetDirectoryName(exePath)!, "plugin");

// agy branch (install)
if (all || agent.Equals("agy", StringComparison.OrdinalIgnoreCase))
    results.Add(Isolate("agy", () =>
    {
        if (install)
        {
            new PluginArtifactWriter(stagingDir).Generate(exePath, ThisVersion());
            // Legacy cleanup (migration sweep) — reuse the retired writer's Uninstall(); swallow.
            new AgyConfigWriter(paths.AgyServers, paths.AgyPerms, paths.AgyPluginsDir).Uninstall();
            RemoveStrayAgyPluginMcpJson(paths.AgyPluginsDir); // deletes {AgyPluginsDir}/flaui-mcp/.mcp.json if present
            return new AgyPluginRegistrar(AgyInvoker()).Register(stagingDir);
        }
        return new AgyPluginRegistrar(AgyInvoker()).Unregister();
    }));

// claude branch (install)
if (all || agent.Equals("claude", StringComparison.OrdinalIgnoreCase))
    results.Add(Isolate("claude", () =>
    {
        var remedy = new ClaudeCollisionRemedy(ClaudeRunner(() => TimeSpan.Zero, ClaudeBudget), paths.StateDir);
        if (install)
        {
            new PluginArtifactWriter(stagingDir).Generate(exePath, ThisVersion()); // idempotent if agy already ran
            new ClaudeCodeConfigWriter(ClaudeRunner(() => TimeSpan.Zero, ClaudeBudget)).Uninstall(); // sweep: claude mcp remove
            new ClaudeSkillDeployer(paths.ClaudeConfigDir).Remove(); // drop legacy ~/.claude/skills/flaui-mcp (skill now ships in plugin)
            remedy.Apply(); // disable legacy flaui-mcp@flaui-mcp copies (id differs from our new one -> never self-disables)
            return new ClaudePluginRegistrar(ClaudeInvoker()).Register(stagingDir);
        }
        // uninstall handled in Task 8
        return new ClaudePluginRegistrar(ClaudeInvoker()).Unregister();
    }));
```

Add the supporting helpers to `CliRouter`:

```csharp
private static string ThisVersion() =>
    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

// Build a CliInvoker for agy/claude. run honors the FAKE_*_PRESENT/MISSING env seams like ClaudeRunner;
// resolve honors presence seams so e2e tests need no real CLI.
private static CliInvoker AgyInvoker()    => BuildInvoker("agy",    "FLAUI_MCP_FAKE_AGY_PRESENT",    "FLAUI_MCP_FAKE_AGY_MISSING");
private static CliInvoker ClaudeInvoker() => BuildInvoker("claude", "FLAUI_MCP_FAKE_CLAUDE_PRESENT", "FLAUI_MCP_FAKE_CLAUDE_MISSING");

private static CliInvoker BuildInvoker(string cli, string presentVar, string missingVar)
{
    var forcedPresent = Environment.GetEnvironmentVariable(presentVar) == "1";
    var forcedMissing = Environment.GetEnvironmentVariable(missingVar) == "1";
    Func<string, string?> resolve = c =>
        forcedMissing ? null : forcedPresent ? $"<fake:{c}>" : CliResolver.Resolve(c);
    Func<string, string[], string?, RunResult> run = (file, args, cwd) =>
        forcedPresent ? new RunResult(0, FakeCliOutput(args)) // seam: succeed, and answer `plugin list` with our id active
                      : ProcessRunner.Run(file, args, cwd, ProcessRunner.DefaultTimeout);
    return new CliInvoker(run, resolve);
}

// Under the PRESENT seam, `plugin list` must report our marketplace id as active so the read-back passes.
private static string FakeCliOutput(string[] args) =>
    System.Array.IndexOf(args, "list") >= 0 ? $"{PluginIds.InstallTarget}  enabled" : "";

private static void RemoveStrayAgyPluginMcpJson(string agyPluginsDir)
{
    try
    {
        var stray = Path.Combine(agyPluginsDir, "flaui-mcp", ".mcp.json");
        if (File.Exists(stray)) File.Delete(stray);
    }
    catch { /* best-effort migration cleanup */ }
}
```

> NOTE: `ClaudeRunner`'s signature is `(Func<TimeSpan> elapsed, TimeSpan budget)`; the `() => TimeSpan.Zero` above is a placeholder budget clock — match how the CURRENT claude branch constructs its runner (`CliRouter.cs:255-256` uses a live `Stopwatch`). Preserve that stopwatch pattern rather than the zero-clock shown here; the zero-clock is only to keep the snippet short.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~CliRouterPluginRegistrationTests"`
Then the full default gate: `dotnet test FlaUI.Mcp.sln --filter "Category!=Desktop&Category!=SyntheticInput"`
Expected: `Passed!  - Failed: 0`. (Existing `CliRouterClaudeSkillTests` that asserted the OLD `claude mcp add` / skills-dir behavior WILL fail — update or delete those assertions in this step, since that behavior is intentionally retired. List each changed test in the commit.)

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Install/
git commit -m "feat(install): canonical INSTALL sequence — generate->sweep->register via CLIs (no hand-written config)"
```

---

### Task 8: Canonical UNINSTALL — deregister-first + conditional staging delete + fail-open warning

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs` (uninstall dispatch `:103-120` and the uninstall half of the branches)
- Test: `test/FlaUI.Mcp.Tests/Install/CliRouterPluginRegistrationTests.cs`

**Step 0 — state-check:** confirm `CliRouter.cs:103-120` still handles `case "uninstall"` by calling `Apply(agent, paths, install:false, exePath)` then writing uninstall warnings. Confirm `installer/flaui-mcp.iss` `[UninstallRun]` (`:44-47`) invokes `flaui-mcp uninstall --agent all` and `ShowUninstallWarnings()` (`:120-154`) reads `{StateDir}/uninstall-warnings.log`. If not, STOP: `STATE_MISMATCH`.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Uninstall_deletes_staging_on_success_and_writes_no_warning()
    {
        // Arrange: run install first (both present), then uninstall.
        var exe = Path.Combine(_root, "flaui-mcp.exe"); File.WriteAllText(exe, "");
        CliRouter.Run(new[] { "install", "--agent", "all" }, exe, new StringWriter());
        var staging = Path.Combine(_root, "plugin");
        Assert.True(Directory.Exists(staging));

        var code = CliRouter.Run(new[] { "uninstall", "--agent", "all" }, exe, new StringWriter());

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(staging)); // deleted on successful deregister
        Assert.False(File.Exists(Path.Combine(_root, "state", "uninstall-warnings.log")));
    }

    [Fact]
    public void Uninstall_leaves_staging_and_warns_when_deregister_fails()
    {
        var exe = Path.Combine(_root, "flaui-mcp.exe"); File.WriteAllText(exe, "");
        CliRouter.Run(new[] { "install", "--agent", "all" }, exe, new StringWriter());
        var staging = Path.Combine(_root, "plugin");

        // Force agy deregister to FAIL for the uninstall run.
        Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_AGY_FAIL", "1");
        try
        {
            CliRouter.Run(new[] { "uninstall", "--agent", "all" }, exe, new StringWriter());
        }
        finally { Environment.SetEnvironmentVariable("FLAUI_MCP_FAKE_AGY_FAIL", null); }

        Assert.True(Directory.Exists(staging)); // left in place — still referenced
        var log = File.ReadAllText(Path.Combine(_root, "state", "uninstall-warnings.log"));
        Assert.Contains(@".gemini", log); // warning names agy's managed dir
        Assert.Contains("agy plugin uninstall", log); // instructs CLI-deregister before manual delete
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~CliRouterPluginRegistrationTests"`
Expected: FAIL — staging not deleted / no warning written.

- [ ] **Step 3: Implement the uninstall path.** In the uninstall half of `Apply`, after the registrars' `Unregister()` results are collected, add conditional deletion + warning. Do it once at the `case "uninstall"` site (`CliRouter.cs:103-120`) where all agent results are available:

```csharp
case "uninstall":
{
    var uninstallResults = Apply(agent, paths, install: false, exePath).ToList();
    Report(uninstallResults, "uninstall", paths.DataDir, outp);

    var stagingDir = Environment.GetEnvironmentVariable("FLAUI_MCP_STAGING_DIR")
                     ?? Path.Combine(Path.GetDirectoryName(exePath)!, "plugin");
    // Deregister is the PRIMARY removal (it drops the loaded copies). Delete the staged build artifact
    // ONLY if every present agent deregistered cleanly; else LEAVE it and warn (still-referenced dir).
    var deregisterFailed = uninstallResults.Any(r => r.Change == AgentChange.Failed);
    if (!deregisterFailed)
    {
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { /* leave on error */ }
    }
    else
    {
        WriteUninstallWarning(paths.StateDir, stagingDir, paths.AgyPluginsDir);
    }

    // (existing) sweep backups; purge data if requested — keep as-is at :113-119.
    if (HasFlag(args, "--purge-data")) PurgeDataDir(paths.DataDir);
    return uninstallResults.Any(r => r.Change == AgentChange.Failed) ? 1 : 0;
}
```

```csharp
private static void WriteUninstallWarning(string stateDir, string stagingDir, string agyPluginsDir)
{
    var managed = Path.Combine(agyPluginsDir, "flaui-mcp");
    var msg =
        "flaui-mcp could not be fully removed from one or more agents. The plugin may still load.\n" +
        "Run these BEFORE manually deleting any directory (deleting a still-referenced dir breaks agent startup):\n" +
        "  claude plugin marketplace remove flaui-mcp-marketplace\n" +
        "  agy plugin uninstall flaui-mcp\n" +
        "Locations, if manual cleanup is still needed afterwards:\n" +
        $"  agy managed copy:   {managed}\n" +
        $"  staged build dir:   {stagingDir}\n";
    try
    {
        Directory.CreateDirectory(stateDir);
        File.AppendAllText(Path.Combine(stateDir, "uninstall-warnings.log"), msg);
    }
    catch { /* best-effort: the warning channel itself must never throw and abort uninstall */ }
}
```

Extend `BuildInvoker` to honor a `FLAUI_MCP_FAKE_AGY_FAIL` / `FLAUI_MCP_FAKE_CLAUDE_FAIL` seam (return `RunResult(1,"forced")` so `Unregister()` yields `Failed`):

```csharp
    var forcedFail = Environment.GetEnvironmentVariable(cli == "agy" ? "FLAUI_MCP_FAKE_AGY_FAIL" : "FLAUI_MCP_FAKE_CLAUDE_FAIL") == "1";
    Func<string, string[], string?, RunResult> run = (file, args, cwd) =>
        forcedFail   ? new RunResult(1, "forced") :
        forcedPresent ? new RunResult(0, FakeCliOutput(args)) :
                        ProcessRunner.Run(file, args, cwd, ProcessRunner.DefaultTimeout);
```

> NOTE: the existing `case "uninstall"` already writes uninstall warnings + sweeps backups (`CliRouter.cs:105-119`). PRESERVE that existing logic; the snippet above ADDS the staging-delete + registration warning around it. Read `:103-120` at Step 0 and integrate, do not wholesale-replace.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FlaUI.Mcp.sln --filter "FullyQualifiedName~CliRouterPluginRegistrationTests"` then the full default gate.
Expected: `Passed!  - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Install/CliRouterPluginRegistrationTests.cs
git commit -m "feat(install): canonical UNINSTALL — deregister-first, conditional staging delete, fail-open warning to state log"
```

---

### Task 9: Update operator-manual + README to the plugin model

**Files:**
- Modify: `docs/operator-manual.md` (§Register `:36-48`, §What-the-installer-changes `:155-162`, env var `:88`, §Uninstall `:165-175`)
- Modify: `README.md` (`:18`)

**Step 0 — state-check:** re-read those line ranges (they may have shifted); confirm they still describe `claude mcp` / the drop-in agy plugins dir / `antigravity-cli/settings.json`. If already updated, STOP: `STATE_MISMATCH`.

- [ ] **Step 1: Edit `docs/operator-manual.md`** — replace the old-mechanism prose:
  - §"Register with Claude Code" / agy sections (`:36-48`): describe registration via `claude plugin marketplace add/install` + `agy plugin install`; remove "static plugin under `%USERPROFILE%\.gemini\config\plugins\flaui-mcp\` … Restart agy".
  - §"What the installer changes" table (`:155-162`): both agent rows become "registers via the agent CLI; writes NO agent MCP config file." Delete the `~/.gemini/settings.json` + `antigravity-cli/settings.json` claims.
  - `FLAUI_MCP_AGY_PLUGINS_DIR` (`:88`): note it is now only used for the legacy-cleanup sweep, not the install target; add `FLAUI_MCP_STAGING_DIR` (overrides the staging dir, default `{app}\plugin`).
  - §Uninstall (`:165-175`): reflect CLI-first deregistration + fail-open leave-and-warn (warning surfaced via the uninstall MsgBox).

- [ ] **Step 2: Edit `README.md:18`** — keep "It configures Claude Code and Antigravity automatically" but ensure nearby text does not imply hand-written config.

- [ ] **Step 3: Verify no stale references remain**

Run: `rg -n "mcp_config.json|settings.json|claude mcp add|config\\\\plugins\\\\flaui-mcp|antigravity-cli" docs/operator-manual.md README.md`
Expected: no lines describing the installer WRITING those files (matches only in historical/ξ context are acceptable; there should be no active instruction). Spec success-criterion #6.

- [ ] **Step 4: Commit**

```bash
git add docs/operator-manual.md README.md
git commit -m "docs: operator-manual + README to the plugin/CLI registration model"
```

---

### Task 10: Bundled clavity fix — commonmemory.iss Excludes

**Files:**
- Modify: `C:\Users\user\Development\Rust\clavity\commonmemory\installer\commonmemory.iss` (line ~31-32, the `Source: "..\*"` `[Files]` entry)

**Step 0 — state-check:** open the file; confirm line ~31-32 is `Source: "..\*"; ... Excludes: "installer,dist,publish"` (or similar) and does NOT already exclude `.claude-plugin`. If it already excludes it, STOP: `STATE_MISMATCH`.

- [ ] **Step 1: Edit** — add `.claude-plugin` to the `Excludes:` list so the dev-clone `marketplace.json` is not shipped into the production plugin dir (nested-manifest violation). Exact change: `Excludes: "installer,dist,publish"` → `Excludes: "installer,dist,publish,.claude-plugin"`.

- [ ] **Step 2: Verify**

Run: `rg -n "Excludes" "C:\Users\user\Development\Rust\clavity\commonmemory\installer\commonmemory.iss"`
Expected: the `Excludes` line now contains `.claude-plugin`.

- [ ] **Step 3: Commit** (in the clavity repo)

```bash
cd /c/Users/user/Development/Rust/clavity && git add commonmemory/installer/commonmemory.iss && git commit -m "fix(commonmemory installer): exclude .claude-plugin from bundled Source (nested-manifest)"
```

---

### Task 11: Version bump + final integration verification

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (version 0.16.1 → 0.16.2)
- Modify: `installer/flaui-mcp.iss` (`:4` `#define AppVersion "0.16.1"` → `"0.16.2"`)
- Modify: `plugins/flaui-mcp/.claude-plugin/plugin.json` (version → 0.16.2)

**Step 0 — state-check:** grep the current version strings; confirm they read `0.16.1`.

Run: `rg -n "0\.16\.1" src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj installer/flaui-mcp.iss plugins/flaui-mcp/.claude-plugin/plugin.json`

- [ ] **Step 1: Bump all three to `0.16.2`.**

- [ ] **Step 2: Full build + default gate**

Run: `dotnet build FlaUI.Mcp.sln -c Debug` → expect `0 Warning(s), 0 Error(s)`.
Run: `dotnet test FlaUI.Mcp.sln --filter "Category!=Desktop&Category!=SyntheticInput"` → expect `Passed!  - Failed: 0`.

- [ ] **Step 3: Manual smoke (console, on the dev box — NOT CI).** After a real `dotnet publish` + installer build, run the installer, then verify BOTH agents load `desktop_*` and NEITHER `~/.gemini/settings.json` nor `~/.gemini/config/mcp_config.json` was written by the installer (grep them for `flaui-mcp`). This is spec success-criteria #1–#4; record the result but it is not a CI gate.

- [ ] **Step 4: Commit**

```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj installer/flaui-mcp.iss plugins/flaui-mcp/.claude-plugin/plugin.json
git commit -m "chore(release): bump to v0.16.2 (installer registration rework)"
```

---

## Self-Review

**Spec coverage** — every spec section maps to a task:
- Unified plugin artifact (plugin.json/.mcp.json/marketplace.json/skill, source=".", JsonSerializer) → Tasks 3-4.
- Registration mechanism (agy/claude CLIs, idempotent remove-then-add, exact argv, read-back active-state) → Tasks 5-6.
- CLI resolution (`cmd /C`, ArgumentList discrete elements, presence gate, NotFound soft-skip) → Tasks 1-2, wired in 7.
- Canonical ordered sequence (generate→cleanup→register / deregister→conditional-delete) → Tasks 7-8.
- Migration sweep (reuse `Uninstall()`, stray `.mcp.json`, before register) → Task 7.
- ClaudeCollisionRemedy (disable-before-register, exclude-self by id distinctness, preserve Restore) → Task 7 (Apply) + existing Restore untouched.
- ClaudeSkillDeployer repoint→drop legacy (skill ships in staging) → Tasks 4 (staging skill) + 7 (`.Remove()` legacy).
- Two-copy uninstall + warning to state log → Task 8.
- Docs update → Task 9. Bundled clavity fix → Task 10. Version → Task 11.

**Placeholder scan:** no "TBD"/"handle errors"; every code step shows the code; the three `> NOTE:` blocks are Step-0 state-checks (implementer verifies exact line/idiom against live code), not deferred logic.

**Type consistency:** `AgentResult`/`AgentChange` members, `RunResult(Code,Output)`, the `Func<string,string[],string?,RunResult>` seam, `CliInvoker.Invoke`/`IsPresent`, `PluginIds.*` constants, and the `flaui-mcp` / `flaui-mcp-marketplace` / `flaui-mcp@flaui-mcp-marketplace` literals are used identically across Tasks 1-8.

**Known integration risks flagged for the implementer (not blockers):**
1. Existing `CliRouterClaudeSkillTests` assert the RETIRED `claude mcp add` + skills-dir behavior — Task 7 Step 4 must update/remove those assertions (they encode the old contract intentionally being replaced).
2. The `ClaudeRunner` stopwatch/budget construction must be preserved (the plan's `() => TimeSpan.Zero` is illustrative — match `CliRouter.cs:255-256`).
3. The embedded-resource name and the skill's H1 substring must be confirmed at Step 0 of Task 4 against `AgyConfigWriter.cs:36`.
4. `agy plugin install`/`claude plugin` argv are grounded in clavity's proven `plugin-registration.iss`; the manual smoke (Task 11 Step 3) is the only place real CLIs run.
