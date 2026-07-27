# Agent-Adoption Activation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the installed `flaui-mcp` desktop tools the agent's default move at the moment of need, by fixing the broken load line, shipping the plugin's hooks for the first time, injecting a ready-to-run payload at session start, and hoisting trap-class facts into tool descriptions.

**Architecture:** Four mechanisms (M0–M3) from the spec, gated behind one precondition. The precondition (G6/G7) is that `PluginArtifactWriter` currently stages only four files, so the plugin ships **no hooks at all** — every hook mechanism in this repo is inert today. Tasks 1–4 fix distribution; only then does the M1 hook become deliverable. M0/M2 edit the skill (whose build input is `.claude/skills/…`, embedded at `csproj:8`); M3 edits C# tool descriptions and is independent of everything else.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), xUnit, `ModelContextProtocol` 1.4.0, Inno Setup, Claude Code plugin manifests.

**Spec:** `docs/superpowers/specs/2026-07-26-agent-adoption-activation-design.md` @ `9285f8f`

**Gate command (repo's default — do not add stricter flags):**

```bash
dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"
```

Expected: `Passed!  - Failed: 0` with zero new warnings.

---

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `.gitattributes` | Pin `*.sh` to LF so shipped scripts never carry CRLF | Create |
| `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` | Embed hooks/scripts/skills as resources | Modify (`:7-11`) |
| `src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs` | Stage the full plugin, not four files | Modify (`:81-120`) |
| `src/FlaUI.Mcp.Server/Install/ActivationPayload.cs` | The compiled-in SessionStart payload constant | Create |
| `src/FlaUI.Mcp.Server/Install/CliRouter.cs` | Add the `activation-payload` verb | Modify (`:29-31`) |
| `src/FlaUI.Mcp.Server/Install/InstallStatus.cs` | Report activation-hook health + version skew | Modify (`:30-34`) |
| `.claude/skills/driving-flaui-mcp/SKILL.md` | **Build input** — the shipped skill | Modify (`:3`, `:25`) |
| `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md` | Repo-checkout twin; must stay identical | Modify (`:3`, `:25`) |
| `src/FlaUI.Mcp.Server/Tools/WindowTools.cs` | M3 trap fact | Modify (`:23`) |
| `src/FlaUI.Mcp.Server/Tools/ContentTools.cs` | M3 trap fact | Modify (`:86`) |
| `test/FlaUI.Mcp.Tests/Install/RepoPaths.cs` | Locate repo-root files from tests | Create |
| `test/FlaUI.Mcp.Tests/Install/PluginDistributionTests.cs` | Criteria 7, 8, 9 | Create |
| `test/FlaUI.Mcp.Tests/Install/SkillLoadLineTests.cs` | Criteria 1, 2, 3 | Create |
| `test/FlaUI.Mcp.Tests/Install/ActivationPayloadTests.cs` | Criterion 4 | Create |
| `test/FlaUI.Mcp.Tests/Server/ToolTrapFactInvariantTests.cs` | Criterion 5 | Create |
| `.claude/recommended-tools.json` | Declare `jq` | Create |
| `installer/flaui-mcp.iss` | Declare `jq` at install time | Modify |

**Convention note:** `test/FlaUI.Mcp.Tests/Install/*.cs` use the **global namespace** (no `namespace` declaration) — see `AgySkillDeployTests.cs:1-5`. Match that.

---

## Task 1: Pin shell line endings

The repo has no `.gitattributes`, and both copies of `flaui-curate-nudge.sh` are CRLF in the working tree.

**This is hygiene, not a bug fix — do not claim the hook is broken.** Measured 2026-07-27: the live copy at `.claude/hooks/flaui-curate-nudge.sh` is CRLF **and runs correctly** (Git Bash tolerates the `\r`; it fired during plan review and wrote its session sentinel). The justification for pinning LF is *portability of a script that is about to ship to arbitrary machines* — bare `bash` on Windows resolves to the WSL launcher before Git Bash, and that path is far less forgiving. Task 3 makes this script ship for the first time, which is what raises the stakes.

**Files:**
- Create: `.gitattributes`
- Modify: `.claude/hooks/flaui-curate-nudge.sh` (normalize to LF — **the live copy, and Task 3's build input**)
- Modify: `plugins/flaui-mcp/scripts/flaui-curate-nudge.sh` (normalize to LF — the twin)

> **Third copy alert.** There are **two** tracked copies of this script — `.claude/hooks/` (live,
> registered by `.claude/settings.json`, proven to fire) and `plugins/flaui-mcp/scripts/` (registered
> nowhere). They are byte-identical today, guarded by no test. Task 3 embeds the **live** copy, so the
> shipped artifact derives from proven-working code; Step 4 below guards the twin against drift.

- [ ] **Step 1: Create `.gitattributes`**

```gitattributes
* text=auto

# Shell scripts ship inside the plugin and are executed by whatever `bash` the
# host resolves. Git Bash tolerates CRLF; other interpreters do not, and on
# Windows a bare `bash` resolves to the WSL launcher before Git Bash. Pin LF so
# the shipped bytes do not depend on the checkout machine's core.autocrlf.
*.sh text eol=lf
```

- [ ] **Step 2: Verify BOTH copies are currently CRLF**

```bash
file .claude/hooks/flaui-curate-nudge.sh plugins/flaui-mcp/scripts/flaui-curate-nudge.sh
```

Expected: both lines contain `with CRLF line terminators`. The live copy is checked first because it is Task 3's build input — normalizing only the twin would leave the shipped bytes unchanged.

- [ ] **Step 3: Renormalize the working tree**

```bash
git add --renormalize .
file .claude/hooks/flaui-curate-nudge.sh plugins/flaui-mcp/scripts/flaui-curate-nudge.sh
```

Expected: neither line contains `CRLF`.

- [ ] **Step 4: Commit**

```bash
git add .gitattributes .claude/hooks/flaui-curate-nudge.sh plugins/flaui-mcp/scripts/flaui-curate-nudge.sh
git commit -m "build: pin *.sh to LF so shipped hook scripts stay portable"
```

> The drift guard for the two script copies lands in **Task 2 Step 3**, not here — it needs the
> `RepoPaths` helper that Task 2 creates. Keeping it here would make Task 1 uncompilable in isolation.

---

## Task 2: Test helper for repo-root paths

Several tests must read files that live in the repo, not in the test output dir.

**Files:**
- Create: `test/FlaUI.Mcp.Tests/Install/RepoPaths.cs`

- [ ] **Step 1: Write the helper**

```csharp
using System;
using System.IO;

/// <summary>Locates files that live in the REPO (not the test output dir), by walking up from the
/// test assembly's base directory until a directory containing FlaUI.Mcp.slnx is found. Tests that
/// assert on tracked source files (SKILL.md copies, plugin tree) need this; tests that only exercise
/// staging dirs do not.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlaUI.Mcp.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                $"repo root (containing FlaUI.Mcp.slnx) not found above {AppContext.BaseDirectory}");
        return dir.FullName;
    }

    public static string At(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());
}
```

> Add `using System.Linq;` to the file's usings — `At` uses `Concat`.

- [ ] **Step 2: Write a test that the helper resolves**

Create `test/FlaUI.Mcp.Tests/Install/RepoPathsTests.cs`:

```csharp
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
}
```

- [ ] **Step 3: Guard the two script copies against drift**

This is the drift guard deferred from Task 1 — it lives here because it needs `RepoPaths`. Append to `test/FlaUI.Mcp.Tests/Install/RepoPathsTests.cs` (inside the class):

```csharp
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
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~RepoPathsTests"`
Expected: PASS, 3 tests. (The identity test passes today — it exists to keep the twin from rotting.)

- [ ] **Step 5: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Install/RepoPaths.cs test/FlaUI.Mcp.Tests/Install/RepoPathsTests.cs
git commit -m "test: add RepoPaths helper and guard the two nudge-script copies against drift"
```

---

## Task 3: Embed the plugin's hooks, scripts and autotrain skills

`csproj:8-10` embeds only the driving skill. The hooks, scripts, and the `flaui-learn`/`flaui-curate` skills are not embedded, so `PluginArtifactWriter` cannot stage them.

**Spec note:** §8 scopes the autotrain skills back IN — shipping the `Stop` curate-nudge hook while withholding the `flaui-curate` skill it invokes is self-contradicting.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj:7-11`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/PluginDistributionTests.cs`:

```csharp
using System.IO;
using System.Linq;
using System.Reflection;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class PluginDistributionTests
{
    private static Assembly ServerAsm => typeof(PluginArtifactWriter).Assembly;

    public static TheoryData<string> RequiredResources() => new()
    {
        "FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md",
        "FlaUI.Mcp.Server.seed.flaui-learn.SKILL.md",
        "FlaUI.Mcp.Server.seed.flaui-curate.SKILL.md",
        "FlaUI.Mcp.Server.seed.hooks.hooks.json",
        "FlaUI.Mcp.Server.seed.scripts.flaui-curate-nudge.sh",
    };

    [Theory]
    [MemberData(nameof(RequiredResources))]
    public void Every_shipped_plugin_artifact_is_embedded(string logicalName)
    {
        using var s = ServerAsm.GetManifestResourceStream(logicalName);
        Assert.True(s is not null,
            $"embedded resource '{logicalName}' missing. Available:\n" +
            string.Join("\n", ServerAsm.GetManifestResourceNames()));
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~PluginDistributionTests"`
Expected: FAIL — 4 of 5 cases report the resource missing (only the driving skill exists).

- [ ] **Step 3: Replace the csproj ItemGroup**

Replace `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` lines 7–11 with:

```xml
  <ItemGroup>
    <!-- The plugin payload is EMBEDDED and extracted byte-for-byte at install time by
         PluginArtifactWriter. The repo tree is the strict build input: never re-author these
         as C# string literals, or the tested copy and the shipped copy drift apart.
         The driving skill's input is .claude/skills/... (NOT plugins/...) -- that path is the
         historical build input and is what ships; the plugins/ copy is its repo-checkout twin. -->
    <EmbeddedResource Include="..\..\.claude\skills\driving-flaui-mcp\SKILL.md">
      <LogicalName>FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md</LogicalName>
    </EmbeddedResource>
    <EmbeddedResource Include="..\..\plugins\flaui-mcp\skills\flaui-learn\SKILL.md">
      <LogicalName>FlaUI.Mcp.Server.seed.flaui-learn.SKILL.md</LogicalName>
    </EmbeddedResource>
    <EmbeddedResource Include="..\..\plugins\flaui-mcp\skills\flaui-curate\SKILL.md">
      <LogicalName>FlaUI.Mcp.Server.seed.flaui-curate.SKILL.md</LogicalName>
    </EmbeddedResource>
    <EmbeddedResource Include="..\..\plugins\flaui-mcp\hooks\hooks.json">
      <LogicalName>FlaUI.Mcp.Server.seed.hooks.hooks.json</LogicalName>
    </EmbeddedResource>
    <!-- The LIVE copy is the build input, not plugins/flaui-mcp/scripts/. .claude/settings.json
         registers this path, so it is the copy the maintainer actually executes and tests; embedding
         it makes the shipped artifact strictly derived from proven-working code. Same principle as
         the driving skill above. Task 1's identity test keeps the plugins/ twin from drifting. -->
    <EmbeddedResource Include="..\..\.claude\hooks\flaui-curate-nudge.sh">
      <LogicalName>FlaUI.Mcp.Server.seed.scripts.flaui-curate-nudge.sh</LogicalName>
    </EmbeddedResource>
  </ItemGroup>
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~PluginDistributionTests"`
Expected: PASS, 5 cases.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj test/FlaUI.Mcp.Tests/Install/PluginDistributionTests.cs
git commit -m "build: embed plugin hooks, scripts and autotrain skills for distribution"
```

---

## Task 4: Stage the full plugin (G6/G7 precondition)

`PluginArtifactWriter.Generate()` (`:81-87`) writes four artifacts. Add hooks, scripts and the two autotrain skills, extracting **byte-for-byte** with LF preserved.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs:13` (add resource ids), `:81-120`

- [ ] **Step 1: Write the failing test**

Append to `test/FlaUI.Mcp.Tests/Install/PluginDistributionTests.cs` (inside the class):

```csharp
    private static string Stage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flaui-stage-" + Path.GetRandomFileName());
        new PluginArtifactWriter(dir).Generate(@"C:\fake\flaui-mcp.exe", "9.9.9");
        return dir;
    }

    [Theory]
    [InlineData("hooks/hooks.json")]
    [InlineData("scripts/flaui-curate-nudge.sh")]
    [InlineData("skills/driving-flaui-mcp/SKILL.md")]
    [InlineData("skills/flaui-learn/SKILL.md")]
    [InlineData("skills/flaui-curate/SKILL.md")]
    public void Generate_stages_every_plugin_artifact(string relative)
    {
        var staged = Path.Combine(Stage(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(staged), $"Generate() did not stage {relative}");
    }

    [Fact]
    public void Staged_shell_scripts_contain_no_CRLF()
    {
        foreach (var sh in Directory.GetFiles(Path.Combine(Stage(), "scripts"), "*.sh"))
            Assert.DoesNotContain("\r", File.ReadAllText(sh));
    }

    [Theory]
    [InlineData("FlaUI.Mcp.Server.seed.scripts.flaui-curate-nudge.sh", "scripts/flaui-curate-nudge.sh")]
    [InlineData("FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md", "skills/driving-flaui-mcp/SKILL.md")]
    public void Staged_artifacts_are_byte_identical_to_their_embedded_source(string resource, string relative)
    {
        var staged = Path.Combine(Stage(), relative.Replace('/', Path.DirectorySeparatorChar));
        using var s = ServerAsm.GetManifestResourceStream(resource)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal(ms.ToArray(), File.ReadAllBytes(staged));
    }
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~PluginDistributionTests"`
Expected: FAIL — staging assertions fail; `Generate()` writes no `hooks/` or `scripts/`.

- [ ] **Step 3: Add the resource ids**

Replace `src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs` line 13 with:

```csharp
    public const string SkillResource   = "FlaUI.Mcp.Server.seed.driving-flaui-mcp.SKILL.md";
    public const string LearnSkill      = "FlaUI.Mcp.Server.seed.flaui-learn.SKILL.md";
    public const string CurateSkill     = "FlaUI.Mcp.Server.seed.flaui-curate.SKILL.md";
    public const string HooksJson       = "FlaUI.Mcp.Server.seed.hooks.hooks.json";
    public const string CurateNudgeSh   = "FlaUI.Mcp.Server.seed.scripts.flaui-curate-nudge.sh";
```

- [ ] **Step 4: Replace `Generate` and `WriteSkill`**

Replace `PluginArtifactWriter.cs` lines **81–121** with the block below. The file is **121 lines**; line 120 closes `WriteSkill` and line 121 closes the class. The replacement block ends with its own class-closing `}`, so replacing only through 120 would leave a duplicate brace and fail with **CS1022**.

```csharp
    public void Generate(string exePath, string version)
    {
        WriteMcpJson(exePath);
        WritePluginJson(version);
        WriteMarketplaceJson();
        WriteSkills();
        WriteHooksAndScripts();
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

    private void WriteSkills()
    {
        Extract(PluginIds.SkillResource, Path.Combine("skills", "driving-flaui-mcp", "SKILL.md"));
        Extract(PluginIds.LearnSkill,    Path.Combine("skills", "flaui-learn", "SKILL.md"));
        Extract(PluginIds.CurateSkill,   Path.Combine("skills", "flaui-curate", "SKILL.md"));
    }

    private void WriteHooksAndScripts()
    {
        Extract(PluginIds.HooksJson,     Path.Combine("hooks", "hooks.json"));
        Extract(PluginIds.CurateNudgeSh, Path.Combine("scripts", "flaui-curate-nudge.sh"));
    }

    /// Copy an embedded resource to a staging-relative path BYTE FOR BYTE. Deliberately a raw stream
    /// copy, not ReadToEnd/WriteAllText: text round-tripping would re-encode newlines and turn a
    /// shipped .sh into CRLF, which bash rejects at the first `\r`.
    private void Extract(string resource, string relativePath)
    {
        var target = Path.Combine(_stagingDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var stream = typeof(PluginArtifactWriter).Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"embedded plugin resource missing: {resource}");
        using var outFile = File.Create(target);
        stream.CopyTo(outFile);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~PluginDistributionTests"`
Expected: PASS, all cases.

- [ ] **Step 6: Run the full gate**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs test/FlaUI.Mcp.Tests/Install/PluginDistributionTests.cs
git commit -m "fix(install): stage plugin hooks, scripts and autotrain skills (G6/G7)"
```

---

## Task 5: M0 — fix the broken load line

`SKILL.md:25` names `mcp__flaui-mcp__desktop_*`, but under plugin registration the tools are `mcp__plugin_flaui-mcp_flaui-mcp__desktop_*`. Run verbatim it returns *no matching deferred tools* — measured live, and the broken line is currently shipped.

`select:` silently ignores names that do not match, so listing **both** forms resolves correctly under either registration.

**Files:**
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md:25`
- Modify: `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md:25`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/SkillLoadLineTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FlaUI.Mcp.Server.Install;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

public class SkillLoadLineTests
{
    private const string BuildInput = ".claude/skills/driving-flaui-mcp/SKILL.md";
    private const string RepoTwin   = "plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md";

    private static string Read(string rel) => File.ReadAllText(RepoPaths.At(rel.Split('/')));

    public static TheoryData<string> BothCopies() => new() { BuildInput, RepoTwin };

    /// Every bare tool name the server actually exposes, from live [McpServerTool] reflection.
    private static HashSet<string> LiveToolNames()
        => typeof(WindowTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttributesData().Any(d => d.AttributeType == typeof(McpServerToolAttribute)))
            .Select(ToolName)
            .ToHashSet(StringComparer.Ordinal);

    // DesktopListWindows -> desktop_list_windows
    private static string ToolName(MethodInfo m)
        => Regex.Replace(m.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    private static IEnumerable<string> SelectNames(string markdown)
        => Regex.Matches(markdown, @"select:([^""\r\n]+)")
                .SelectMany(mt => mt.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(s => s.Trim());

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void No_single_prefix_form_remains(string rel)
        => Assert.DoesNotContain("select:mcp__flaui-mcp__", Read(rel));

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Every_named_tool_is_derivable_from_live_reflection(string rel)
    {
        var live = LiveToolNames();
        var bad = SelectNames(Read(rel)).Where(n =>
        {
            var bare = n.StartsWith("mcp__plugin_flaui-mcp_flaui-mcp__") ? n["mcp__plugin_flaui-mcp_flaui-mcp__".Length..]
                     : n.StartsWith("mcp__flaui-mcp__")                  ? n["mcp__flaui-mcp__".Length..]
                     : null;
            return bare is null || !live.Contains(bare);
        }).ToList();

        Assert.True(bad.Count == 0,
            $"{rel}: load-line names not derivable from a live [McpServerTool]:\n" + string.Join("\n", bad));
    }

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Both_prefix_forms_are_present_for_every_named_tool(string rel)
    {
        var names = SelectNames(Read(rel)).ToHashSet(StringComparer.Ordinal);
        var missing = names
            .Where(n => n.StartsWith("mcp__flaui-mcp__"))
            .Select(n => "mcp__plugin_flaui-mcp_flaui-mcp__" + n["mcp__flaui-mcp__".Length..])
            .Where(expected => !names.Contains(expected))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{rel}: plugin-prefixed twin missing for:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void The_two_tracked_copies_are_byte_identical()
        => Assert.Equal(
            File.ReadAllBytes(RepoPaths.At(BuildInput.Split('/'))),
            File.ReadAllBytes(RepoPaths.At(RepoTwin.Split('/'))));
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~SkillLoadLineTests"`
Expected: FAIL on `No_single_prefix_form_remains` and `Both_prefix_forms_are_present_for_every_named_tool`.

- [ ] **Step 3: Replace line 25 in BOTH copies**

In `.claude/skills/driving-flaui-mcp/SKILL.md` **and** `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md`, replace line 25 with this single line:

```
ToolSearch "select:mcp__flaui-mcp__desktop_list_windows,mcp__plugin_flaui-mcp_flaui-mcp__desktop_list_windows,mcp__flaui-mcp__desktop_open_window,mcp__plugin_flaui-mcp_flaui-mcp__desktop_open_window,mcp__flaui-mcp__desktop_snapshot,mcp__plugin_flaui-mcp_flaui-mcp__desktop_snapshot,mcp__flaui-mcp__desktop_get_text,mcp__plugin_flaui-mcp_flaui-mcp__desktop_get_text,mcp__flaui-mcp__desktop_input_status,mcp__plugin_flaui-mcp_flaui-mcp__desktop_input_status"
```

Immediately below the closing ``` fence (after line 26), insert:

```markdown
Both prefixes are listed on purpose: the tools are named `mcp__plugin_flaui-mcp_flaui-mcp__*` when the
plugin is registered and `mcp__flaui-mcp__*` when the server is registered directly. `select:` ignores
names it cannot match, so listing both resolves under either without noise. If it returns **no matches**,
retry `ToolSearch "desktop window snapshot"` and use ONLY these five names — `desktop_list_windows`,
`desktop_open_window`, `desktop_snapshot`, `desktop_get_text`, `desktop_input_status`. That keyword search
also returns window-mutating tools; do not substitute a similar-looking name for one that is missing.
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~SkillLoadLineTests"`
Expected: PASS, all cases (including `The_two_tracked_copies_are_byte_identical`).

- [ ] **Step 5: Commit**

```bash
git add .claude/skills/driving-flaui-mcp/SKILL.md plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md test/FlaUI.Mcp.Tests/Install/SkillLoadLineTests.cs
git commit -m "fix(skill): load line resolves under plugin AND direct registration (M0/G3)"
```

---

## Task 6: M2 — monologue-matched skill frontmatter

`SKILL.md:3` reads *"Use when driving or dogfooding…"* — it matches only once the agent has already decided to drive. Rewrite it question-shaped and whole-surface, but **bounded**: a spurious match loads 340 lines. The literal token `Get-Process` is forbidden — the matcher indexes it heavily and would drag this skill into every background-process check.

**Files:**
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md:3`
- Modify: `plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md:3`

- [ ] **Step 1: Write the failing test**

Append to `test/FlaUI.Mcp.Tests/Install/SkillLoadLineTests.cs` (inside the class):

```csharp
    private static string Frontmatter(string rel)
    {
        var text = Read(rel);
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        Assert.True(end > 0, $"{rel}: no closing frontmatter fence");
        return text[..end];
    }

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Frontmatter_does_not_embed_the_over_indexing_process_token(string rel)
        => Assert.DoesNotContain("Get-Process", Frontmatter(rel), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Frontmatter_is_decision_point_shaped_not_mechanism_shaped(string rel)
    {
        var fm = Frontmatter(rel);
        Assert.DoesNotContain("Use when driving or dogfooding", fm);
        foreach (var cue in new[] { "on screen", "running", "terminal", "click" })
            Assert.Contains(cue, fm, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(BothCopies))]
    public void Frontmatter_description_stays_within_budget(string rel)
    {
        var desc = Frontmatter(rel).Split('\n').First(l => l.StartsWith("description:", StringComparison.Ordinal));
        Assert.True(desc.Length <= 600, $"{rel}: description is {desc.Length} chars (budget 600)");
    }
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~SkillLoadLineTests"`
Expected: FAIL on `Frontmatter_is_decision_point_shaped_not_mechanism_shaped`.

- [ ] **Step 3: Replace line 3 in BOTH copies**

```
description: Use when you need to know what is on the Windows screen, whether a desktop app is running or responding, what a background terminal or console tab shows, or when you need to click, type into, or confirm a change landed in a real GUI app. Look and act yourself with the installed flaui-mcp desktop_* tools rather than asking the user to observe or operate their desktop for you. Covers read-only perception, synthetic input under a lease, and focus/ref recovery.
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~SkillLoadLineTests"`
Expected: PASS, all cases (byte-identity still holds — both copies changed identically).

- [ ] **Step 5: Commit**

```bash
git add .claude/skills/driving-flaui-mcp/SKILL.md plugins/flaui-mcp/skills/driving-flaui-mcp/SKILL.md test/FlaUI.Mcp.Tests/Install/SkillLoadLineTests.cs
git commit -m "fix(skill): question-shaped frontmatter so the matcher fires at the decision point (M2)"
```

---

## Task 7: M1 — the activation payload constant

The payload is a compiled-in constant, not a shell script: no `bash` (which resolves to WSL first on Windows), no `jq`, no CRLF hazard.

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/ActivationPayload.cs`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/ActivationPayloadTests.cs`:

```csharp
using System;
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

    /// REVISED DURING EXECUTION. The original asserted `Text.Length <= 1200` — unmeetable by this
    /// plan's own payload (measured 1434, of which the irreducible load line is 456). The budget now
    /// measures PROSE ONLY, excluding the load line, because a raw total conflates mechanical API
    /// verbosity with conceptual bloat; the line count is the real anti-creep guard. See spec §5.2
    /// "Bounded length". Do NOT revert this to a total-length check.
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
            var bare = token[(token.LastIndexOf("__", StringComparison.Ordinal) + 2)..].TrimEnd(':', '.', ';');
            Assert.Contains(bare, allowed);
        }
    }

    [Fact]
    public void Tells_the_agent_not_to_delegate_observation_to_the_human()
        => Assert.Contains("Never ask the user", ActivationPayload.Text, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ActivationPayloadTests"`
Expected: FAIL — `ActivationPayload` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

```csharp
using System.Text.Json;

namespace FlaUI.Mcp.Server.Install;

/// <summary>The SessionStart payload. It is a PAYLOAD, not a signpost: a skimmed reminder leaves
/// nothing behind, whereas a skimmed payload still leaves an executable load line in context.
///
/// Compiled in rather than shipped as a script deliberately — a hook command of the form
/// `bash "..."` has no determinate interpreter on Windows (bare `bash` resolves to the WSL launcher
/// before Git Bash), and a script extracted with CRLF dies at its first `\r`.
///
/// Budgets (asserted by ActivationPayloadTests): 15 lines / 1200 chars. SessionStart hooks BLOCK the
/// first turn — measured — so this must stay cheap and must never grow into a second copy of the skill.</summary>
public static class ActivationPayload
{
    private const string P = "mcp__flaui-mcp__desktop_";
    private const string Q = "mcp__plugin_flaui-mcp_flaui-mcp__desktop_";

    private static string Pair(string tool) => $"{P}{tool},{Q}{tool}";

    /// Both prefixes: tools are plugin-prefixed when registered as a plugin, bare when registered
    /// directly. `select:` ignores names it cannot match, so naming both is registration-agnostic.
    private static readonly string LoadLine =
        $"ToolSearch \"select:{Pair("list_windows")},{Pair("open_window")},{Pair("snapshot")}," +
        $"{Pair("get_text")},{Pair("input_status")}\"";

    public static readonly string Text = string.Join("\n", new[]
    {
        "flaui-mcp is installed: you can see and operate this Windows desktop yourself.",
        "Never ask the user to look at, read, or click inside a desktop app on your behalf, and do not infer UI state indirectly from process lists.",
        "Triggers: what is on screen; is an app running or responding; what a background terminal/console tab shows; clicking, typing or filling a GUI dialog; confirming a change landed in the real app.",
        "Load the tools (one call):",
        LoadLine,
        "If that returns no matches, retry ToolSearch \"desktop window snapshot\" and use ONLY: desktop_list_windows, desktop_open_window, desktop_snapshot, desktop_get_text, desktop_input_status. If one is absent, say so — never substitute a similar name.",
        "Read-only perception needs no lease and cannot disturb the user: desktop_list_windows(includeHandles:true) then desktop_snapshot wN then desktop_get_text wN eN.",
        "Typing, clicking, dragging, or reading a BACKGROUND terminal tab all need a lease — use the driving-flaui-mcp skill for those.",
    });

    public static string ToJson() => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new { hookEventName = "SessionStart", additionalContext = Text }
    });
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ActivationPayloadTests"`
Expected: PASS, 4 tests.

> If `Stays_within_the_injected_text_budget` fails, shorten the **prose** lines — never drop a prefix pair from `LoadLine`, which would reintroduce G3.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ActivationPayload.cs test/FlaUI.Mcp.Tests/Install/ActivationPayloadTests.cs
git commit -m "feat(install): compiled-in SessionStart activation payload (M1)"
```

---

## Task 8: M1 — the `activation-payload` verb

The verb must return before any MCP/DI initialisation. `CliRouter.cs:29-31` (`print-config`) is the model: a pure-output early-return verb in the same switch.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/CliRouter.cs:29-31`

> **Two gates, not one.** `Program.cs:13` dispatches to the router only when
> `CliRouter.IsInstallerVerb(args)` returns true, and that method (`CliRouter.cs:11`) tests a hardcoded
> `Verbs` HashSet at `CliRouter.cs:8-10`. **Adding a `case` to the switch is not sufficient** — a verb
> missing from `Verbs` falls straight through to MCP server startup, so the hook would hang at every
> session start instead of printing JSON. Both edits are required.

- [ ] **Step 1: Write the failing test**

Append to `test/FlaUI.Mcp.Tests/Install/ActivationPayloadTests.cs` (inside the class):

```csharp
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
```

> Add `using System.IO;` to the test file — `StringWriter`.
>
> These two tests are the point of the task: `The_verb_name_is_stable` alone would pass **without** any
> router change, and a test that cannot fail is worse than no test.

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ActivationPayloadTests"`
Expected: FAIL — `ActivationPayload.Verb` does not exist (compile error), and once it does, both router tests fail.

- [ ] **Step 3: Add the constant**

In `src/FlaUI.Mcp.Server/Install/ActivationPayload.cs`, immediately after `public static class ActivationPayload\n{`, add:

```csharp
    /// The CLI verb the generated SessionStart hook invokes. Named once so hooks.json generation and
    /// the router can never drift apart.
    public const string Verb = "activation-payload";
```

- [ ] **Step 4a: Register the verb (REQUIRED — the switch case alone does nothing)**

In `src/FlaUI.Mcp.Server/Install/CliRouter.cs`, replace the `Verbs` initializer at lines 8–10 with:

```csharp
    private static readonly HashSet<string> Verbs =
        new(StringComparer.OrdinalIgnoreCase) { "install", "uninstall", "print-config", "status", "unlock", "lock", "overlay", "autosound", "presence", ActivationPayload.Verb, "--version", "-v", "--help", "-h" };
```

- [ ] **Step 4b: Add the router case**

In `src/FlaUI.Mcp.Server/Install/CliRouter.cs`, immediately after the `print-config` case (which ends `return 0;` at line 31), insert:

```csharp
            // Emitted into a SessionStart hook. Returns HERE, before any MCP/DI/server setup: the
            // client BLOCKS the first turn on hook output, so every millisecond is user-visible wait.
            case ActivationPayload.Verb:
                outp.WriteLine(ActivationPayload.ToJson());
                return 0;
```

- [ ] **Step 5: Run the test and exercise the verb**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ActivationPayloadTests"`
Expected: PASS, 5 tests.

Run: `dotnet run --project src/FlaUI.Mcp.Server -- activation-payload`
Expected: one line of JSON containing `"hookEventName":"SessionStart"`.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ActivationPayload.cs src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Install/ActivationPayloadTests.cs
git commit -m "feat(cli): add activation-payload verb for the SessionStart hook (M1)"
```

---

## Task 9: M1 — generate `hooks.json` with the SessionStart entry

The staged `hooks.json` must carry a `SessionStart` hook invoking the **installed exe by absolute path** — the same path `.mcp.json` already receives. So `hooks.json` is **generated**, not extracted; Task 4's embedded copy remains the source of the `Stop` hook entry.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs` (`WriteHooksAndScripts`)

- [ ] **Step 1: Write the failing test**

Append to `test/FlaUI.Mcp.Tests/Install/PluginDistributionTests.cs` (inside the class):

```csharp
    [Fact]
    public void Staged_hooks_json_wires_SessionStart_to_the_installed_exe()
    {
        var json = File.ReadAllText(Path.Combine(Stage(), "hooks", "hooks.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var sessionStart = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
        var command = sessionStart[0].GetProperty("hooks")[0].GetProperty("command").GetString();

        Assert.Contains(@"C:\fake\flaui-mcp.exe", command);
        Assert.Contains("activation-payload", command);
    }

    [Fact]
    public void Staged_hooks_json_preserves_the_existing_Stop_hook()
    {
        var json = File.ReadAllText(Path.Combine(Stage(), "hooks", "hooks.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("hooks").TryGetProperty("Stop", out _),
            "generation dropped the curate-nudge Stop hook");
    }
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~PluginDistributionTests"`
Expected: FAIL — the staged `hooks.json` has no `SessionStart` key.

- [ ] **Step 3: Replace `WriteHooksAndScripts`**

```csharp
    private void WriteHooksAndScripts(string exePath)
    {
        Extract(PluginIds.CurateNudgeSh, Path.Combine("scripts", "flaui-curate-nudge.sh"));

        // hooks.json is GENERATED, not extracted: the SessionStart command needs the installed exe's
        // ABSOLUTE path, which only exists at install time — the same value .mcp.json already gets.
        // The embedded copy supplies every other hook (currently the curate-nudge Stop hook), so a
        // hook added to the repo tree ships without touching this method.
        using var stream = typeof(PluginArtifactWriter).Assembly.GetManifestResourceStream(PluginIds.HooksJson)
            ?? throw new FileNotFoundException($"embedded plugin resource missing: {PluginIds.HooksJson}");
        using var reader = new StreamReader(stream);
        var root = JsonNode.Parse(reader.ReadToEnd())!;

        var hooks = root["hooks"]!.AsObject();
        hooks["SessionStart"] = new JsonArray
        {
            new JsonObject
            {
                // matcher copied from the WORKING SessionStart hook already in this repo
                // (.claude/settings.json) — a proven shape, not an invented one. All three sources
                // matter: compact and clear are exactly when the agent has just lost its context.
                ["matcher"] = "startup|clear|compact",
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"]    = "command",
                        // Serialized via JsonNode, so the Windows backslashes are escaped correctly.
                        ["command"] = $"\"{exePath}\" {ActivationPayload.Verb}",
                    }
                }
            }
        };

        var target = Path.Combine(_stagingDir, "hooks", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, root.ToJsonString(Pretty));
    }
```

Update the call site in `Generate` from `WriteHooksAndScripts();` to `WriteHooksAndScripts(exePath);`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~PluginDistributionTests"`
Expected: PASS, all cases.

> `Staged_artifacts_are_byte_identical_to_their_embedded_source` covers the `.sh` and the skill, **not** `hooks.json` — that file is now generated by design.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/PluginArtifactWriter.cs test/FlaUI.Mcp.Tests/Install/PluginDistributionTests.cs
git commit -m "feat(install): wire the SessionStart activation hook into the staged plugin (M1)"
```

---

## Task 10: M3 — hoist the trap fact into tool descriptions

`WindowTools.cs:23` already carries *"A Hint field may accompany multiplexer windows (e.g. Windows Terminal) noting the listing shows only the active tab"* — and the motivating failure happened anyway. Placement is not enough: the fact must be an **imperative naming the wrong default**.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Tools/WindowTools.cs:23`
- Modify: `src/FlaUI.Mcp.Server/Tools/ContentTools.cs:86`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Server/ToolTrapFactInvariantTests.cs`:

```csharp
using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

/// <summary>Structural invariant: trap-class facts must be present in the tool DESCRIPTIONS, which are
/// the only surface that re-enters context every time a tool loads and cannot be held stale, skimmed or
/// truncated. The assertions target the IMPERATIVE clause, not a keyword — a passive "X may occur" note
/// was already present when the motivating failure happened, so a keyword check would pass on text that
/// demonstrably does not change behaviour.</summary>
public class ToolTrapFactInvariantTests
{
    private const int DescriptionBudget = 1200;

    private static string Description(Type type, string method)
    {
        var m = type.GetMethod(method) ?? throw new InvalidOperationException($"{type.Name}.{method} not found");
        var d = m.GetCustomAttribute<DescriptionAttribute>();
        Assert.True(d is not null, $"{type.Name}.{method} has no [Description]");
        return d!.Description;
    }

    [Theory]
    [InlineData(typeof(WindowTools), nameof(WindowTools.DesktopListWindows))]
    [InlineData(typeof(ContentTools), nameof(ContentTools.DesktopReadTerminalTab))]
    public void Terminal_tab_trap_is_stated_as_an_imperative(Type type, string method)
    {
        var text = Description(type, method);
        Assert.Contains("launcher, not the program", text, StringComparison.Ordinal);
        Assert.Contains("read every candidate", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_tool_description_exceeds_the_budget()
    {
        var over = typeof(WindowTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttributesData().Any(d => d.AttributeType == typeof(McpServerToolAttribute)))
                .Select(m => (Name: $"{t.Name}.{m.Name}", Len: m.GetCustomAttribute<DescriptionAttribute>()?.Description.Length ?? 0)))
            .Where(x => x.Len > DescriptionBudget)
            .Select(x => $"{x.Name} = {x.Len} chars")
            .ToList();

        Assert.True(over.Count == 0,
            $"tool descriptions over {DescriptionBudget} chars — hoisting must not metastasize:\n"
            + string.Join("\n", over));
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ToolTrapFactInvariantTests"`
Expected: FAIL on `Terminal_tab_trap_is_stated_as_an_imperative` for both tools.

- [ ] **Step 3: Amend `WindowTools.cs:23`**

Replace the final sentence of the `DesktopListWindows` description — *"A Hint field may accompany multiplexer windows (e.g. Windows Terminal) noting the listing shows only the active tab."* — with:

```
A Hint field may accompany multiplexer windows (e.g. Windows Terminal) noting the listing shows only the active tab. A terminal tab title names the launcher, not the program: a tab titled cmd.exe or PowerShell may be hosting a running CLI agent, so never conclude a program is absent from titles alone — enumerate the tabs and read every candidate.
```

- [ ] **Step 4: Amend `ContentTools.cs:86`**

Replace *"tabIndex only (no ref/title — refs go stale on switch, titles are ambiguous); enumerate tabs with desktop_snapshot first."* with:

```
tabIndex only (no ref/title — refs go stale on switch, titles are ambiguous); enumerate tabs with desktop_snapshot first. The tab title names the launcher, not the program, so read every candidate tab before concluding the program you want is not running.
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~ToolTrapFactInvariantTests"`
Expected: PASS, 3 tests.

> If `No_tool_description_exceeds_the_budget` fails for `DesktopReadTerminalTab`, that description is the practical ceiling — trim its least load-bearing clause rather than raising `DescriptionBudget`.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Tools/WindowTools.cs src/FlaUI.Mcp.Server/Tools/ContentTools.cs test/FlaUI.Mcp.Tests/Server/ToolTrapFactInvariantTests.cs
git commit -m "fix(tools): state the terminal-tab trap as an imperative in tool descriptions (M3)"
```

---

## Task 11: Report activation-hook health and version skew in `status`

A skill change only reaches a user via rebuild **and** reinstall. Nothing surfaces that today, so a user can silently run an old skill against a new server.

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/InstallStatus.cs:30-34`

- [ ] **Step 1: Write the failing test**

Create `test/FlaUI.Mcp.Tests/Install/InstallStatusActivationTests.cs`:

```csharp
using System.IO;
using FlaUI.Mcp.Server.Install;
using Xunit;

public class InstallStatusActivationTests
{
    private static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), "flaui-status-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Reports_the_activation_hook_as_absent_when_the_plugin_is_not_staged()
    {
        var text = InstallStatus.Describe(@"C:\fake\flaui-mcp.exe", Temp(), Temp(), Temp(), Temp(),
                                          ClaudePluginStatus.NotRegistered);
        Assert.Contains("Activation hook:", text);
        Assert.Contains("not staged", text);
    }

    [Fact]
    public void Reports_the_activation_hook_as_wired_when_the_staged_hooks_file_names_the_verb()
    {
        var staging = Temp();
        new PluginArtifactWriter(staging).Generate(@"C:\fake\flaui-mcp.exe", "9.9.9");

        var text = InstallStatus.DescribeActivationHook(staging);
        Assert.Contains("wired", text);
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~InstallStatusActivationTests"`
Expected: FAIL — `DescribeActivationHook` does not exist.

- [ ] **Step 3: Add the reporter**

In `src/FlaUI.Mcp.Server/Install/InstallStatus.cs`, add this method to the class:

```csharp
    /// <summary>Answers "why is the activation hint not appearing?" without reading hook source.
    /// Reads the STAGED hooks.json — the artifact the client actually loads — not the repo tree,
    /// which is registered nowhere.</summary>
    public static string DescribeActivationHook(string pluginStagingDir)
    {
        var hooks = Path.Combine(pluginStagingDir, "hooks", "hooks.json");
        if (!File.Exists(hooks))
            return "not staged — run `flaui-mcp install --agent claude` to (re)generate the plugin";

        var text = File.ReadAllText(hooks);
        if (!text.Contains(ActivationPayload.Verb, StringComparison.Ordinal))
            return "staged but NOT wired — hooks.json has no SessionStart entry; reinstall to regenerate";

        return "wired (SessionStart -> flaui-mcp " + ActivationPayload.Verb + ")";
    }
```

The staging dir must be derived **exactly** as `install` derives it, or `status` will report on a directory the installer never wrote. Verified at `CliRouter.cs:273-274`: it is `FLAUI_MCP_STAGING_DIR` if set, else `<dir-of-exe>\plugin`. Both `Describe` and `install` already have `exePath`, so no new parameter is needed — but the derivation must live in **one** place. Add to `PluginArtifactWriter.cs`, inside `PluginIds`:

```csharp
    /// The isolated staging dir the plugin artifacts are generated into — the SINGLE definition,
    /// shared by install (CliRouter) and status (InstallStatus) so the two can never disagree about
    /// which directory to write and inspect. FLAUI_MCP_STAGING_DIR redirects it so tests never touch
    /// a real install tree.
    public static string StagingDir(string exePath)
        => System.Environment.GetEnvironmentVariable("FLAUI_MCP_STAGING_DIR")
           ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exePath)!, "plugin");
```

Then replace the two local derivations in `CliRouter.cs` (`:273-274` and `:428`) with `var stagingDir = PluginIds.StagingDir(exePath);`, and in `InstallStatus.Describe(...)`, immediately after the existing `sb.AppendLine("  " + DescribeClaudeSkill(...));` block (`:34`), add:

```csharp
        sb.AppendLine("  Activation hook: " + DescribeActivationHook(PluginIds.StagingDir(exePath)));
```

> `PluginIds` is `internal`; `InstallStatus` and `CliRouter` are in the same assembly, so this compiles. The test in Step 1 calls `InstallStatus.DescribeActivationHook` directly with an explicit dir, so it needs no access to `PluginIds`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlaUI.Mcp.slnx --filter "FullyQualifiedName~InstallStatusActivationTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/InstallStatus.cs test/FlaUI.Mcp.Tests/Install/InstallStatusActivationTests.cs
git commit -m "feat(status): report activation-hook health so a silent hook is diagnosable"
```

---

## Task 12: Declare `jq` as a prerequisite

The staged `Stop` hook pipes through `jq`, which is absent on a stock Windows host. Both targets are **net-new** — `.claude/recommended-tools.json` does not exist, and `installer/flaui-mcp.iss` has no prerequisite-check mechanism (its only `Check:` is `NeedsAddPath` at `:34`).

**Files:**
- Create: `.claude/recommended-tools.json`
- Modify: `installer/flaui-mcp.iss`

- [ ] **Step 1: Create the tool declaration**

```json
{
  "tools": [
    {
      "name": "jq",
      "why": "The plugin's Stop hook (flaui-curate-nudge.sh) parses its stdin JSON with jq. Without it the autotrain nudge silently never fires. The SessionStart activation hook does NOT need jq.",
      "install": "winget install jqlang.jq",
      "in_path": "jq"
    }
  ]
}
```

- [ ] **Step 2: Add the installer check**

In `installer/flaui-mcp.iss`, add to the `[Code]` section:

```pascal
function JqOnPath(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/C where jq >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

procedure CheckOptionalPrereqs();
begin
  if not JqOnPath() then
    MsgBox('Optional prerequisite missing: jq is not on PATH.'#13#10#13#10 +
           'flaui-mcp installs and runs fine without it, but the autotrain nudge hook ' +
           'will not fire. Install it later with:  winget install jqlang.jq',
           mbInformation, MB_OK);
end;
```

Call it from the existing post-install step — in `CurStepChanged`, under `ssPostInstall`, add `CheckOptionalPrereqs();`. If no `CurStepChanged` exists, add:

```pascal
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    CheckOptionalPrereqs();
end;
```

- [ ] **Step 3: Verify the installer still compiles**

Run: `iscc /Qp installer/flaui-mcp.iss`
Expected: compiles with no errors. (If Inno Setup is not installed locally, note it and defer to CI.)

- [ ] **Step 4: Commit**

```bash
git add .claude/recommended-tools.json installer/flaui-mcp.iss
git commit -m "build: declare jq as an optional prerequisite (G5)"
```

---

## Task 13: Full gate and manual install smoke

- [ ] **Step 1: Run the full gate**

Run: `dotnet test FlaUI.Mcp.slnx --filter "Category!=Desktop&Category!=SyntheticInput&Category!=KnownDefect"`
Expected: `Failed: 0`, zero new warnings.

- [ ] **Step 2: Build and stage locally**

```bash
dotnet build src/FlaUI.Mcp.Server -c Release
dotnet run --project src/FlaUI.Mcp.Server -c Release -- activation-payload
```

Expected: one line of JSON with `"hookEventName":"SessionStart"`.

- [ ] **Step 2b: Confirm the verb's latency matches the measured baseline**

**Already measured, 2026-07-27 at 45–65% CPU** (spec §5.2): the shared early-return code path runs in **min 227 / median 313 / max 401 ms** warm. That was taken against `--version`; this step confirms the real verb lands in the same band and does not drag in initialisation work.

**Cold start remains unmeasured at low load** — take an opportunistic reading after the next reboot and record it in §5.2.

Run:

```powershell
$cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
"CPU load: $cpu%"    # abort and retry later if this is not in single digits
$exe = "$env:LOCALAPPDATA\Programs\FlaUI.Mcp\flaui-mcp.exe"
$t = 1..8 | ForEach-Object {
  $sw=[Diagnostics.Stopwatch]::StartNew(); & $exe activation-payload | Out-Null; $sw.Stop()
  [int]$sw.ElapsedMilliseconds
}
"samples ms: $($t -join ', ')"
"warm median ms: $(($t | Sort-Object)[[int]($t.Count/2)])"
```

Record the CPU load, the full sample list, the cold (first) sample and the warm median **in the PR description**. Then update §5.2 of the spec, replacing the lower-bound paragraph with the measured figures and dropping the "a true idle measurement is still owed" sentence.

**If the warm median exceeds ~500 ms**, do not silently accept it: report it, because a blocking per-session cost of that size is a design question (§5.2's cost budget), not an implementation detail.

- [ ] **Step 3: Manual install smoke — NOT CI-gatable, do not skip**

`claude plugin install` has no CI equivalent. Run by hand and record the result:

```bash
flaui-mcp install --agent claude
flaui-mcp status
```

Expected: `status` reports the plugin active **and** `Activation hook: wired`. Then confirm on disk:

```bash
ls "$LOCALAPPDATA/Programs/FlaUI.Mcp/plugin/hooks/hooks.json"
grep -c "mcp__plugin_flaui-mcp_flaui-mcp__" "$LOCALAPPDATA/Programs/FlaUI.Mcp/plugin/skills/driving-flaui-mcp/SKILL.md"
```

Expected: the hooks file exists; the grep returns `6` — five plugin-prefixed tool names in the load
line, plus one mention in the explanatory paragraph beneath it.

> **The original command here was wrong and would have looked like a failure.** It grepped for
> `select:mcp__plugin_flaui-mcp`, which **cannot match**: `select:` is immediately followed by the
> **bare** prefix, because the bare form leads each pair. Run against a correctly-shipped skill it
> returns `0`. This is the same misconception that made Task 5's original
> `DoesNotContain("select:mcp__flaui-mcp__")` assertion unsatisfiable — caught here only because the
> smoke was actually executed against a real install.

- [ ] **Step 4: Observational check — the only signal that measures the actual goal**

Start a **fresh** Claude Code session in any project. Confirm the activation text is present, then pose a task needing desktop context. Success: a `ToolSearch` for the desktop tools occurs with **no preceding** ask-the-human attempt. Record the outcome in the PR description; this is a dogfooding gate, not a test.

- [ ] **Step 5: Commit any fixes and push the branch**

```bash
git add -A
git commit -m "chore: post-smoke fixes for agent-adoption activation"
```

---

## Self-Review

**Spec coverage.** M0 → Task 5. M1 → Tasks 7, 8, 9 (+ 11 for diagnosability). M2 → Task 6. M3 → Task 10. G4 → Task 5's byte-identity test. G5 → Task 12. G6/G7 → Tasks 1, 3, 4, 9. §8's scoped-back-in autotrain skills → Tasks 3, 4. Acceptance criteria 1–10 all map: 1→T5, 2→T5, 3→T5, 4→T7/T8, 5→T10, 6→T12, 7→T4, 8→T1/T4, 9→T4/T13, 10→T13.

**Placeholders.** None remain. The one open item (Task 11's staging-dir derivation) was closed by reading `CliRouter.cs:273-274` rather than guessed, and hardened into a single shared `PluginIds.StagingDir(exePath)` so `install` and `status` cannot drift apart.

**Known tight constraint — flagged, not hidden.** ~~The activation payload's 1200-char budget is nearly exhausted by design: the dual-prefix load line alone is ~445 characters (10 fully-qualified tool names), leaving ~750 for all prose.~~

**RESOLVED DURING EXECUTION — the budget could NOT be met, and the escalation clause below fired.** Measured: the load line is **456** characters (not ~445) and the payload as specified is **1434** against a 1200 budget — this plan's own content missed its own budget by 19%, and neither the 8-round panel nor agy's plan review caught it. Compressing the prose to fit landed on *exactly* 1200 with zero headroom.

Diagnosis: the instrument was wrong, not just the number. A raw character count conflates mechanical API verbosity (456 chars expressing one concept) with conceptual bloat. **Resolution (agy consulted, user decided): budget the PROSE only, at ≤ 1100 chars excluding the load line, keeping ≤ 15 lines as the primary anti-creep guard.** Measured on landing: 973 prose chars, 8 lines. Rejected: raising the total to 1500 — it re-creates the same squeeze the first time a sixth tool joins the allow-list. Spec §5.2 and §7 criterion 4 updated.

The rule that made this work is retained: shorten prose rather than drop a prefix pair — dropping one would silently reintroduce G3, the defect this whole plan exists to fix. If a budget genuinely cannot be met, that is a spec change (§5.2), not an implementer decision.

**Verified citations.** Every line reference in this plan was read before writing: `csproj:7-11`, `PluginArtifactWriter.cs:13/81-120`, `CliRouter.cs:29-31/39/233/273-274/428`, `InstallStatus.cs:30-34`, `WindowTools.cs:23`, `ContentTools.cs:86`, `ToolReadOnlyInvariantTests.cs`, `installer/flaui-mcp.iss:34`, both `SKILL.md` copies (`:3`, `:25`), and `AgySkillDeployTests.cs:1-5` for the namespace convention.

**One spec citation corrected.** Spec §9 lists the skill's frontmatter `description` at `SKILL.md:20`; it is actually at **line 3** (verified in both copies). The plan uses the correct line. This is a stale citation in the spec, not a design defect — no re-green owed.

**Deliberately deferred to execution, with owners.** (a) Whether the client repeats the orphaned-hook warning every session — Task 13's manual smoke observes it; the spec already records it as cosmetic. (b) The idle latency measurement — **Task 13 Step 2b**, with the exact command, the abort condition, and instructions to update spec §5.2 with the result.

---

## Plan review — folded (agy, convergent line-by-line)

Fired per AGY-AFTER. The consult timed out as `possible_modal`; recovered by (1) reading agy's terminal tab directly with `desktop_read_terminal_tab` — it was **idle, no modal**, in a tab titled `C:\WINDOWS\system32\cmd.exe`, which is *exactly* the launcher-not-the-program trap M3 exists to fix — and (2) a retrieval-only re-ask, which returned the full review intact. Both recoveries were the user's suggestion; neither had occurred to the driver.

| Finding | Verdict | Fold |
|---|---|---|
| Task 4 Step 4 replaces lines 81–120, leaving a duplicate class brace → CS1022 | **Confirmed** (file is 121 lines, not agy's stated 122) | Range corrected to 81–121 with the reason stated |
| Task 8's only test asserts a constant and would pass without the router change | **Confirmed** — a test that cannot fail | Two real router tests added, exercising `CliRouter.Run` |
| Task 13 claims to cover the idle latency measurement but never takes it | **Confirmed** | Step 2b added with the command, abort condition, and spec-update instruction |
| **Missed by both panels** — `Program.cs:13` gates on `CliRouter.IsInstallerVerb`, whose `Verbs` set (`CliRouter.cs:8-10`) would not contain the new verb, so the switch case would never execute and the hook would hang at every session start | **Found while verifying agy's citations** | Step 4a added registering the verb; a test asserts `IsInstallerVerb` accepts it |

agy reported *Ordering hazard* and *Type/signature inconsistency* as clean.

**Pattern, third occurrence this subproject:** agy's conclusion was right and its cited number was wrong (122 vs 121 lines). Verifying the citation rather than the claim is what surfaced the fourth finding — the one that would have cost the most to debug.

**Type consistency.** `ActivationPayload.Text` / `.ToJson()` / `.Verb` are used identically in Tasks 7, 8, 9, 11. `PluginIds` resource constants introduced in Task 4 are reused verbatim in Tasks 4 and 9. `Extract(resource, relativePath)` is defined once (Task 4) and used in Tasks 4 and 9. `RepoPaths.At(...)` is defined in Task 2 and used in Tasks 5 and 6. `Stage()` is defined in Task 4 and reused in Task 9.
