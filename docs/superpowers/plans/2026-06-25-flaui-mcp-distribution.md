# FlaUI.Mcp — Distribution & Installation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship FlaUI.Mcp as a one-download Windows install: a self-contained `flaui-mcp.exe` that is also its own installer (`flaui-mcp install --agent all`), wrapped by an Inno Setup `flaui-mcp-setup.exe`, published by release CI on version tags.

**Architecture:** Add an `Install/` namespace to `FlaUI.Mcp.Server` — a JSONC-safe atomic config-file helper, three per-agent config writers (agy / generic / Claude Code), and a `CliRouter`. `Program.cs` gains an arg branch: a known verb runs the installer; no-args/unknown runs the MCP stdio host (unchanged). The exe publishes self-contained single-file; an Inno script bundles it, runs `install` post-install and `uninstall` on removal. Release CI builds both artifacts and attaches them to a GitHub Release.

**Tech Stack:** `net10.0-windows`, C#, `System.Text.Json` (`JsonNode`), xUnit; Inno Setup; PowerShell; GitHub Actions.

**Reference spec:** `docs/superpowers/specs/2026-06-25-flaui-mcp-distribution-design.md`

**Builds on:** Phase 1 (merged at `e7f7232`). Verified current state: `src/FlaUI.Mcp.Server/Program.cs` is 19 lines of top-level statements ending `await builder.Build().RunAsync();`; `Server.csproj` `<PropertyGroup>` is lines 12–19; the test project references Server.

---

## File Structure

```
src/FlaUI.Mcp.Server/
├─ Program.cs                       # MODIFY: arg branch (verb → installer; else host)
├─ FlaUI.Mcp.Server.csproj          # MODIFY: AssemblyName flaui-mcp + version
└─ Install/
   ├─ JsoncFile.cs                  # load JSONC → JsonObject; atomic save + backup
   ├─ McpServerEntry.cs             # the {command,args} shape + JSON helpers
   ├─ AgentResult.cs                # structured install/uninstall result
   ├─ GenericMcpConfigWriter.cs     # generic mcpServers writer + print-config
   ├─ AgyConfigWriter.cs            # mcpServers + permissions.allow (two files)
   ├─ ClaudeCodeConfigWriter.cs     # `claude mcp add/remove` via injected runner
   └─ CliRouter.cs                  # parse verbs, dispatch, format output
test/FlaUI.Mcp.Tests/Install/       # one test file per unit (temp dirs, no real agents)
installer/flaui-mcp.iss             # Inno Setup script
dist/install.ps1                    # optional silent one-liner wrapper
.github/workflows/release.yml       # release CI
```

**Open item carried from spec (resolve at implementation, no code change needed):** agy's
config-file precedence. `AgyConfigWriter` is constructed with explicit `mcpServersPath` and
`permissionsPath` (may be the same file), so the question is answered by *configuration* in
`CliRouter`, not by editing tested logic. Default in `CliRouter`: `mcpServers` →
`~/.gemini/settings.json`, `permissions` → `~/.gemini/antigravity-cli/settings.json` (the
split observed on the dev machine). **Task 7 includes a STOP-and-verify step against live agy.**

**Spec softening (flagged):** the spec's "comments survive" is not achievable with
`System.Text.Json` (it reserializes; comments are dropped). The implementable contract is
**JSONC tolerated on read + all data keys preserved + a timestamped backup before every
write**. Tests assert that, not comment survival. (Full comment preservation = v2 nicety.)

---

## Task 0: Publish properties on the Server csproj

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj:12-19`

- [ ] **Step 1: Add assembly name + version + single-file friendliness**

In `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj`, inside the existing `<PropertyGroup>`
(currently lines 12–19, containing `OutputType`, `TargetFramework`, `Nullable`,
`LangVersion`, `ImplicitUsings`, `ApplicationManifest`), add these lines:
```xml
<AssemblyName>flaui-mcp</AssemblyName>
<Version>0.1.0</Version>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<SelfContained>false</SelfContained>
<InvariantGlobalization>true</InvariantGlobalization>
```
(`SelfContained=false` keeps normal `dotnet build`/`dotnet test` framework-dependent; the
release publish passes `--self-contained` explicitly. `RuntimeIdentifier` pins the build to
win-x64, which the single-file publish needs.)

- [ ] **Step 2: Verify build still succeeds**

Run: `dotnet build src/FlaUI.Mcp.Server`
Expected: `Build succeeded`, 0 errors. The produced assembly is now `flaui-mcp.dll`.

- [ ] **Step 3: Verify a self-contained single-file publish works (smoke)**

Run:
```bash
dotnet publish src/FlaUI.Mcp.Server -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$LOCALAPPDATA/flaui-mcp-pubtest"
```
Expected: `Build succeeded`; a single `flaui-mcp.exe` exists in the output dir. Then confirm
it starts as an MCP server and does not crash:
```bash
echo "" | "$LOCALAPPDATA/flaui-mcp-pubtest/flaui-mcp.exe" &
sleep 2 && kill %1 2>/dev/null || true
```
Expected: process starts and waits on stdio (no crash/stacktrace). If single-file publish
errors on a FlaUI/COM native dependency → STOP and report (the spec assumed this is clean).

- [ ] **Step 4: Commit**

```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj
git commit -m "build(server): AssemblyName flaui-mcp + win-x64 publish properties"
```

---

## Task 1: Trait-split the UIA tests so CI can skip them

**Files:**
- Modify: `test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs`
- Modify: `test/FlaUI.Mcp.Tests/Windows/WindowOperationsTests.cs`
- Modify: `test/FlaUI.Mcp.Tests/Server/WindowToolsTests.cs`

These three classes touch real windows/UIA; CI runners are headless. Tag them so the release
workflow can run `--filter Category!=Desktop`. The non-UIA tests (threading, errors, session,
matcher, and everything in Task 2+) stay unfiltered.

- [ ] **Step 1: Add the Desktop trait to each UIA test class**

In each of the three files, add `[Trait("Category", "Desktop")]` immediately above the
`public class ...` declaration. Example for `WindowManagerTests.cs`:
```csharp
[Trait("Category", "Desktop")]
public class WindowManagerTests : IClassFixture<TestAppFixture>
```
Do the same for `public class WindowOperationsTests` and `public class WindowToolsTests`.
(`Trait` comes from `Xunit`, already globally imported via the test csproj `<Using>`.)

- [ ] **Step 2: Verify the filter selects only non-UIA tests**

Run: `dotnet test --filter "Category!=Desktop"`
Expected: PASS, and the UIA tests are NOT run — the passing count is the non-UIA subset
(StaThreadContext 3, ToolException 2, AutomationDispatcher 3, SessionManager 2,
LaunchedWindowMatcher 5 = 15), `Failed: 0`. (Window/WindowOps/WindowTools are skipped.)

- [ ] **Step 3: Commit**

```bash
git add test/FlaUI.Mcp.Tests/Windows/WindowManagerTests.cs test/FlaUI.Mcp.Tests/Windows/WindowOperationsTests.cs test/FlaUI.Mcp.Tests/Server/WindowToolsTests.cs
git commit -m "test: tag UIA tests [Trait(Category,Desktop)] so CI can skip them"
```

---

## Task 2: `JsoncFile` — JSONC-tolerant load + atomic backed-up save

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/JsoncFile.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/JsoncFileTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json.Nodes;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class JsoncFileTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"flaui-jsonc-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_missing_file_returns_empty_object()
    {
        var obj = JsoncFile.Load(TempFile());
        Assert.Empty(obj);
    }

    [Fact]
    public void Load_tolerates_comments_and_trailing_commas()
    {
        var path = TempFile();
        File.WriteAllText(path, "{\n  // a comment\n  \"a\": 1,\n  \"b\": 2,\n}");
        try
        {
            var obj = JsoncFile.Load(path);
            Assert.Equal(1, (int)obj["a"]!);
            Assert.Equal(2, (int)obj["b"]!);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_preserves_unrelated_keys_and_writes_a_backup()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ \"keep\": \"me\" }");
        try
        {
            var obj = JsoncFile.Load(path);
            obj["added"] = "new";
            JsoncFile.Save(path, obj);

            var reloaded = JsoncFile.Load(path);
            Assert.Equal("me", (string)reloaded["keep"]!);
            Assert.Equal("new", (string)reloaded["added"]!);
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".bak-*"));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
                File.Delete(f);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~JsoncFileTests`
Expected: FAIL — `JsoncFile` does not exist (compile error).

- [ ] **Step 3: Implement `JsoncFile`**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Loads a JSON/JSONC file as a mutable <see cref="JsonObject"/>, tolerating comments and
/// trailing commas, and saves it atomically (temp + move) after a timestamped backup.
/// NOTE: comments are NOT preserved across a save (System.Text.Json reserializes) — the
/// pre-write backup `<file>.bak-<timestamp>` is the recovery path; all data keys ARE kept.
/// </summary>
public static class JsoncFile
{
    private static readonly JsonDocumentOptions DocOpts =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        var node = JsonNode.Parse(text, nodeOptions: null, documentOptions: DocOpts);
        return node as JsonObject
            ?? throw new InvalidOperationException($"{path} is not a JSON object.");
    }

    public static void Save(string path, JsonObject obj)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path))
            File.Copy(path, $"{path}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~JsoncFileTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/JsoncFile.cs test/FlaUI.Mcp.Tests/Install/JsoncFileTests.cs
git commit -m "feat(install): JsoncFile - JSONC-tolerant load + atomic backed-up save"
```

---

## Task 3: `McpServerEntry` + `AgentResult` value types

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/McpServerEntry.cs`
- Create: `src/FlaUI.Mcp.Server/Install/AgentResult.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/McpServerEntryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class McpServerEntryTests
{
    [Fact]
    public void ToJsonNode_emits_command_and_empty_args()
    {
        var node = McpServerEntry.ForExe(@"C:\tools\flaui-mcp.exe").ToJsonNode();
        Assert.Equal(@"C:\tools\flaui-mcp.exe", (string)node["command"]!);
        Assert.Empty(node["args"]!.AsArray());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~McpServerEntryTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement both types**

`McpServerEntry.cs`:
```csharp
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>The MCP server definition agents store under their `mcpServers` map.</summary>
public sealed record McpServerEntry(string Command)
{
    public const string ServerName = "flaui-mcp";

    public static McpServerEntry ForExe(string exePath) => new(exePath);

    public JsonObject ToJsonNode() =>
        new() { ["command"] = Command, ["args"] = new JsonArray() };
}
```

`AgentResult.cs`:
```csharp
namespace FlaUI.Mcp.Server.Install;

public enum AgentChange { Created, Updated, Unchanged, Removed, NotFound }

/// <summary>Outcome of an install/uninstall against one agent's config file(s).</summary>
public sealed record AgentResult(string Agent, AgentChange Change, string Detail);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~McpServerEntryTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/McpServerEntry.cs src/FlaUI.Mcp.Server/Install/AgentResult.cs test/FlaUI.Mcp.Tests/Install/McpServerEntryTests.cs
git commit -m "feat(install): McpServerEntry + AgentResult value types"
```

---

## Task 4: `GenericMcpConfigWriter`

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/GenericMcpConfigWriter.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/GenericMcpConfigWriterTests.cs`

Writes the standard `mcpServers.flaui-mcp` entry into a given JSON config file; `print-config`
returns the snippet; uninstall removes only that key.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json.Nodes;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class GenericMcpConfigWriterTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"flaui-gen-{Guid.NewGuid():N}.json");
    private static void Clean(string p) { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(p)!, Path.GetFileName(p) + "*")) File.Delete(f); }

    [Fact]
    public void Install_creates_then_is_idempotent()
    {
        var path = TempFile();
        try
        {
            var w = new GenericMcpConfigWriter();
            var r1 = w.Install(path, @"C:\x\flaui-mcp.exe");
            Assert.Equal(AgentChange.Created, r1.Change);

            var obj = JsoncFile.Load(path);
            Assert.Equal(@"C:\x\flaui-mcp.exe",
                (string)obj["mcpServers"]!["flaui-mcp"]!["command"]!);

            var r2 = w.Install(path, @"C:\x\flaui-mcp.exe");
            Assert.Equal(AgentChange.Unchanged, r2.Change);
        }
        finally { Clean(path); }
    }

    [Fact]
    public void Uninstall_removes_only_the_flaui_key()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ \"mcpServers\": { \"other\": { \"command\": \"o\" } } }");
        try
        {
            var w = new GenericMcpConfigWriter();
            w.Install(path, @"C:\x\flaui-mcp.exe");
            var r = w.Uninstall(path);
            Assert.Equal(AgentChange.Removed, r.Change);

            var obj = JsoncFile.Load(path);
            Assert.False(obj["mcpServers"]!.AsObject().ContainsKey("flaui-mcp"));
            Assert.True(obj["mcpServers"]!.AsObject().ContainsKey("other"));
        }
        finally { Clean(path); }
    }

    [Fact]
    public void PrintConfig_returns_snippet_with_the_exe()
    {
        var snippet = new GenericMcpConfigWriter().PrintConfig(@"C:\x\flaui-mcp.exe");
        Assert.Contains("mcpServers", snippet);
        Assert.Contains("flaui-mcp", snippet);
        Assert.Contains(@"C:\\x\\flaui-mcp.exe", snippet); // JSON-escaped backslashes
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~GenericMcpConfigWriterTests`
Expected: FAIL — `GenericMcpConfigWriter` does not exist.

- [ ] **Step 3: Implement `GenericMcpConfigWriter`**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>Writes the standard `mcpServers.flaui-mcp` entry into any MCP-client JSON config.</summary>
public sealed class GenericMcpConfigWriter
{
    public AgentResult Install(string configPath, string exePath)
    {
        var obj = JsoncFile.Load(configPath);
        var servers = obj["mcpServers"] as JsonObject;
        if (servers is null) { servers = new JsonObject(); obj["mcpServers"] = servers; }

        var existing = servers[McpServerEntry.ServerName] as JsonObject;
        var desired = McpServerEntry.ForExe(exePath).ToJsonNode();
        if (existing is not null && existing.ToJsonString() == desired.ToJsonString())
            return new AgentResult("generic", AgentChange.Unchanged, configPath);

        var change = existing is null ? AgentChange.Created : AgentChange.Updated;
        servers[McpServerEntry.ServerName] = desired;
        JsoncFile.Save(configPath, obj);
        return new AgentResult("generic", change, configPath);
    }

    public AgentResult Uninstall(string configPath)
    {
        var obj = JsoncFile.Load(configPath);
        if (obj["mcpServers"] is not JsonObject servers || !servers.ContainsKey(McpServerEntry.ServerName))
            return new AgentResult("generic", AgentChange.NotFound, configPath);
        servers.Remove(McpServerEntry.ServerName);
        JsoncFile.Save(configPath, obj);
        return new AgentResult("generic", AgentChange.Removed, configPath);
    }

    public string PrintConfig(string exePath)
    {
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject { [McpServerEntry.ServerName] = McpServerEntry.ForExe(exePath).ToJsonNode() }
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~GenericMcpConfigWriterTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/GenericMcpConfigWriter.cs test/FlaUI.Mcp.Tests/Install/GenericMcpConfigWriterTests.cs
git commit -m "feat(install): GenericMcpConfigWriter - mcpServers entry + print-config + targeted uninstall"
```

---

## Task 5: `AgyConfigWriter` — two edits across (possibly) two files

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/AgyConfigWriterTests.cs`

Antigravity needs `mcpServers.flaui-mcp` **and** `permissions.allow` to contain
`mcp(flaui-mcp/*)`. The two may live in different files, so the writer takes both paths.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json.Nodes;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class AgyConfigWriterTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"flaui-agy-{Guid.NewGuid():N}.json");
    private static void Clean(string p) { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(p)!, Path.GetFileName(p) + "*")) File.Delete(f); }

    [Fact]
    public void Install_writes_both_mcpServers_and_permission_allow()
    {
        var servers = TempFile();
        var perms = TempFile();
        try
        {
            var r = new AgyConfigWriter(servers, perms).Install(@"C:\x\flaui-mcp.exe");
            Assert.Equal(AgentChange.Created, r.Change);

            var s = JsoncFile.Load(servers);
            Assert.Equal(@"C:\x\flaui-mcp.exe", (string)s["mcpServers"]!["flaui-mcp"]!["command"]!);

            var p = JsoncFile.Load(perms);
            var allow = p["permissions"]!["allow"]!.AsArray().Select(n => (string)n!).ToList();
            Assert.Contains("mcp(flaui-mcp/*)", allow);
        }
        finally { Clean(servers); Clean(perms); }
    }

    [Fact]
    public void Install_does_not_duplicate_the_permission_on_rerun()
    {
        var servers = TempFile();
        var perms = TempFile();
        try
        {
            var w = new AgyConfigWriter(servers, perms);
            w.Install(@"C:\x\flaui-mcp.exe");
            w.Install(@"C:\x\flaui-mcp.exe");
            var allow = JsoncFile.Load(perms)["permissions"]!["allow"]!.AsArray()
                .Select(n => (string)n!).Count(s => s == "mcp(flaui-mcp/*)");
            Assert.Equal(1, allow);
        }
        finally { Clean(servers); Clean(perms); }
    }

    [Fact]
    public void Uninstall_removes_both_and_preserves_other_permissions()
    {
        var servers = TempFile();
        var perms = TempFile();
        File.WriteAllText(perms, "{ \"permissions\": { \"allow\": [ \"command(git status)\" ] } }");
        try
        {
            var w = new AgyConfigWriter(servers, perms);
            w.Install(@"C:\x\flaui-mcp.exe");
            w.Uninstall();

            var s = JsoncFile.Load(servers);
            Assert.False((s["mcpServers"] as JsonObject)?.ContainsKey("flaui-mcp") ?? false);

            var allow = JsoncFile.Load(perms)["permissions"]!["allow"]!.AsArray().Select(n => (string)n!).ToList();
            Assert.DoesNotContain("mcp(flaui-mcp/*)", allow);
            Assert.Contains("command(git status)", allow);
        }
        finally { Clean(servers); Clean(perms); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AgyConfigWriterTests`
Expected: FAIL — `AgyConfigWriter` does not exist.

- [ ] **Step 3: Implement `AgyConfigWriter`**

```csharp
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Antigravity (Gemini-CLI-style) writer. Two edits: `mcpServers.flaui-mcp` in the servers
/// file, and `mcp(flaui-mcp/*)` appended to `permissions.allow` in the permissions file
/// (the two paths may be the same file). After install the caller must tell the user to
/// restart agy so the tool registry re-initializes.
/// </summary>
public sealed class AgyConfigWriter
{
    private const string Permission = "mcp(flaui-mcp/*)";
    private readonly string _serversPath;
    private readonly string _permsPath;

    public AgyConfigWriter(string mcpServersPath, string permissionsPath)
    {
        _serversPath = mcpServersPath;
        _permsPath = permissionsPath;
    }

    public AgentResult Install(string exePath)
    {
        // Edit 1: mcpServers (servers file).
        var sObj = JsoncFile.Load(_serversPath);
        var servers = sObj["mcpServers"] as JsonObject;
        if (servers is null) { servers = new JsonObject(); sObj["mcpServers"] = servers; }
        var existing = servers[McpServerEntry.ServerName] as JsonObject;
        var desired = McpServerEntry.ForExe(exePath).ToJsonNode();
        bool serversChanged = existing is null || existing.ToJsonString() != desired.ToJsonString();
        if (serversChanged) { servers[McpServerEntry.ServerName] = desired; JsoncFile.Save(_serversPath, sObj); }

        // Edit 2: permissions.allow (permissions file — reload separately in case it is the same file).
        var pObj = JsoncFile.Load(_permsPath);
        var permissions = pObj["permissions"] as JsonObject;
        if (permissions is null) { permissions = new JsonObject(); pObj["permissions"] = permissions; }
        var allow = permissions["allow"] as JsonArray;
        if (allow is null) { allow = new JsonArray(); permissions["allow"] = allow; }
        bool hasPerm = allow.Any(n => (string?)n == Permission);
        if (!hasPerm) { allow.Add(Permission); JsoncFile.Save(_permsPath, pObj); }

        var change = (serversChanged || !hasPerm)
            ? (existing is null ? AgentChange.Created : AgentChange.Updated)
            : AgentChange.Unchanged;
        return new AgentResult("agy", change, $"{_serversPath}; {_permsPath}");
    }

    public AgentResult Uninstall()
    {
        var sObj = JsoncFile.Load(_serversPath);
        bool removedServer = sObj["mcpServers"] is JsonObject servers && servers.Remove(McpServerEntry.ServerName);
        if (removedServer) JsoncFile.Save(_serversPath, sObj);

        var pObj = JsoncFile.Load(_permsPath);
        bool removedPerm = false;
        if (pObj["permissions"] is JsonObject permissions && permissions["allow"] is JsonArray allow)
        {
            for (int i = allow.Count - 1; i >= 0; i--)
                if ((string?)allow[i] == Permission) { allow.RemoveAt(i); removedPerm = true; }
            if (removedPerm) JsoncFile.Save(_permsPath, pObj);
        }

        return new AgentResult("agy",
            removedServer || removedPerm ? AgentChange.Removed : AgentChange.NotFound,
            $"{_serversPath}; {_permsPath}");
    }
}
```

> NOTE: when `_serversPath == _permsPath`, Edit 1 saves the file, then Edit 2 reloads it
> (picking up Edit 1's write) before appending the permission and saving again — so a shared
> file ends with both changes. The two tests use distinct files; a single-file scenario is
> exercised manually in Task 12 if agy turns out to use one file.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AgyConfigWriterTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/AgyConfigWriter.cs test/FlaUI.Mcp.Tests/Install/AgyConfigWriterTests.cs
git commit -m "feat(install): AgyConfigWriter - mcpServers + permissions.allow, idempotent, targeted uninstall"
```

---

## Task 6: `ClaudeCodeConfigWriter` — via injected command runner

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/ClaudeCodeConfigWriter.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudeCodeConfigWriterTests.cs`

Uses the stable `claude mcp add/remove` CLI rather than guessing Claude Code's config file.
The runner is injected (a delegate) so it is unit-testable without the `claude` CLI present.

- [ ] **Step 1: Write the failing tests**

```csharp
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class ClaudeCodeConfigWriterTests
{
    [Fact]
    public void Install_invokes_claude_mcp_add_with_the_exe()
    {
        var calls = new List<(string file, string[] args)>();
        var w = new ClaudeCodeConfigWriter((file, args) => { calls.Add((file, args)); return 0; });

        var r = w.Install(@"C:\x\flaui-mcp.exe");

        Assert.Equal(AgentChange.Created, r.Change);
        var (file, args) = Assert.Single(calls);
        Assert.Equal("claude", file);
        Assert.Equal(new[] { "mcp", "add", "flaui-mcp", "--", @"C:\x\flaui-mcp.exe" }, args);
    }

    [Fact]
    public void Uninstall_invokes_claude_mcp_remove()
    {
        var calls = new List<string[]>();
        var w = new ClaudeCodeConfigWriter((_, args) => { calls.Add(args); return 0; });
        w.Uninstall();
        Assert.Equal(new[] { "mcp", "remove", "flaui-mcp" }, Assert.Single(calls));
    }

    [Fact]
    public void Install_reports_NotFound_when_runner_signals_missing_cli()
    {
        var w = new ClaudeCodeConfigWriter((_, _) => -1); // -1 == claude CLI unavailable
        var r = w.Install(@"C:\x\flaui-mcp.exe");
        Assert.Equal(AgentChange.NotFound, r.Change);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ClaudeCodeConfigWriterTests`
Expected: FAIL — `ClaudeCodeConfigWriter` does not exist.

- [ ] **Step 3: Implement `ClaudeCodeConfigWriter`**

```csharp
using System.Diagnostics;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Configures Claude Code via its stable `claude mcp add/remove` CLI. The runner returns the
/// process exit code, or -1 if the `claude` CLI is not found. Injected for testability.
/// </summary>
public sealed class ClaudeCodeConfigWriter
{
    private readonly Func<string, string[], int> _run;

    public ClaudeCodeConfigWriter(Func<string, string[], int>? runner = null)
        => _run = runner ?? DefaultRunner;

    public AgentResult Install(string exePath)
    {
        var code = _run("claude", new[] { "mcp", "add", McpServerEntry.ServerName, "--", exePath });
        return code switch
        {
            0  => new AgentResult("claude", AgentChange.Created, "claude mcp add"),
            -1 => new AgentResult("claude", AgentChange.NotFound, "claude CLI not on PATH"),
            _  => new AgentResult("claude", AgentChange.Unchanged, $"claude mcp add exit {code}")
        };
    }

    public AgentResult Uninstall()
    {
        var code = _run("claude", new[] { "mcp", "remove", McpServerEntry.ServerName });
        return code == -1
            ? new AgentResult("claude", AgentChange.NotFound, "claude CLI not on PATH")
            : new AgentResult("claude", AgentChange.Removed, $"claude mcp remove exit {code}");
    }

    private static int DefaultRunner(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return -1; } // executable not found
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ClaudeCodeConfigWriterTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudeCodeConfigWriter.cs test/FlaUI.Mcp.Tests/Install/ClaudeCodeConfigWriterTests.cs
git commit -m "feat(install): ClaudeCodeConfigWriter via claude mcp add/remove (injected runner)"
```

---

## Task 7: `CliRouter` — parse verbs, resolve defaults, dispatch

**Files:**
- Create: `src/FlaUI.Mcp.Server/Install/CliRouter.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/CliRouterTests.cs`

`CliRouter.IsInstallerVerb(args)` decides whether `Program.cs` runs the installer or the host.
`CliRouter.Run(args, exePath, textOut)` executes the verb. Agent default paths live here (so
the agy precedence question is a config default, not tested logic).

- [ ] **Step 1: Write the failing tests**

```csharp
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class CliRouterTests
{
    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("print-config")]
    [InlineData("--version")]
    [InlineData("--help")]
    public void IsInstallerVerb_true_for_known_verbs(string verb)
        => Assert.True(CliRouter.IsInstallerVerb(new[] { verb }));

    [Theory]
    [InlineData(new string[0])]
    [InlineData(new[] { "--unexpected" })]
    public void IsInstallerVerb_false_for_no_args_or_unknown(string[] args)
        => Assert.False(CliRouter.IsInstallerVerb(args));

    [Fact]
    public void Version_prints_and_returns_zero()
    {
        var sb = new StringWriter();
        var code = CliRouter.Run(new[] { "--version" }, @"C:\x\flaui-mcp.exe", sb);
        Assert.Equal(0, code);
        Assert.Contains("flaui-mcp", sb.ToString());
    }

    [Fact]
    public void PrintConfig_generic_emits_snippet()
    {
        var sb = new StringWriter();
        var code = CliRouter.Run(new[] { "print-config", "--agent", "generic" }, @"C:\x\flaui-mcp.exe", sb);
        Assert.Equal(0, code);
        Assert.Contains("mcpServers", sb.ToString());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~CliRouterTests`
Expected: FAIL — `CliRouter` does not exist.

- [ ] **Step 3: Implement `CliRouter`** (complete — no placeholders)

```csharp
namespace FlaUI.Mcp.Server.Install;

/// <summary>Parses the installer CLI verbs and dispatches to the per-agent writers.</summary>
public static class CliRouter
{
    private static readonly HashSet<string> Verbs =
        new(StringComparer.OrdinalIgnoreCase) { "install", "uninstall", "print-config", "--version", "-v", "--help", "-h" };

    public static bool IsInstallerVerb(string[] args) => args.Length > 0 && Verbs.Contains(args[0]);

    public static int Run(string[] args, string exePath, TextWriter outp)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "--help";
        var agent = OptionValue(args, "--agent") ?? "all";
        var configOverride = OptionValue(args, "--config");

        switch (verb)
        {
            case "--version":
            case "-v":
                outp.WriteLine($"flaui-mcp {ThisVersion()}");
                return 0;

            case "print-config":
                outp.WriteLine(new GenericMcpConfigWriter().PrintConfig(exePath));
                return 0;

            case "install":
                foreach (var r in Apply(agent, configOverride, install: true, exePath))
                    outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");
                outp.WriteLine("If you configured agy, restart it to load the new tools.");
                return 0;

            case "uninstall":
                foreach (var r in Apply(agent, configOverride, install: false, exePath))
                    outp.WriteLine($"[{r.Agent}] {r.Change}: {r.Detail}");
                return 0;

            default:
                outp.WriteLine("usage: flaui-mcp [install|uninstall|print-config|--version] [--agent agy|generic|claude|all] [--config <path>]");
                return 0;
        }
    }

    private static IEnumerable<AgentResult> Apply(string agent, string? configOverride, bool install, string exePath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // agy precedence DEFAULT (see STOP-and-verify below). Split observed on the dev machine.
        var agyServers = configOverride ?? Path.Combine(home, ".gemini", "settings.json");
        var agyPerms   = configOverride ?? Path.Combine(home, ".gemini", "antigravity-cli", "settings.json");
        var genericPath = configOverride ?? Path.Combine(home, ".flaui-mcp", "generic-mcp.json");

        var results = new List<AgentResult>();
        bool all = agent.Equals("all", StringComparison.OrdinalIgnoreCase);

        if (all || agent.Equals("agy", StringComparison.OrdinalIgnoreCase))
        {
            var w = new AgyConfigWriter(agyServers, agyPerms);
            results.Add(install ? w.Install(exePath) : w.Uninstall());
        }
        if (all || agent.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            var w = new ClaudeCodeConfigWriter();
            results.Add(install ? w.Install(exePath) : w.Uninstall());
        }
        if (all || agent.Equals("generic", StringComparison.OrdinalIgnoreCase))
        {
            var w = new GenericMcpConfigWriter();
            results.Add(install ? w.Install(genericPath, exePath) : w.Uninstall(genericPath));
        }
        return results;
    }

    private static string? OptionValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string ThisVersion() =>
        typeof(CliRouter).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
```

> **STOP-and-verify (open item):** before considering agy done, confirm on a live agy whether
> `mcpServers` and `permissions.allow` are read from `~/.gemini/settings.json`,
> `~/.gemini/antigravity-cli/settings.json`, or split as defaulted here. Adjust ONLY these two
> default paths (`agyServers`, `agyPerms`) if reality differs — do not change `AgyConfigWriter`'s
> tested logic. If you cannot verify, leave the defaults and note it in your report.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~CliRouterTests`
Expected: PASS (the Theory rows + Version + PrintConfig). If the `--version` test sees
`0.0.0`, that is fine — the assertion only checks for `flaui-mcp`.

- [ ] **Step 5: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CliRouter.cs test/FlaUI.Mcp.Tests/Install/CliRouterTests.cs
git commit -m "feat(install): CliRouter - verb parsing + per-agent dispatch (agy path defaults)"
```

---

## Task 8: Wire the arg branch into `Program.cs`

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Program.cs:1-19`

- [ ] **Step 1: Add the installer branch before the host wiring**

Replace the entire contents of `src/FlaUI.Mcp.Server/Program.cs` (currently 19 lines) with:
```csharp
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Windows;
using FlaUI.Mcp.Server.Install;
using FlaUI.Mcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Installer verbs run and exit; anything else (including no args) runs the MCP stdio host.
if (CliRouter.IsInstallerVerb(args))
{
    var exePath = Environment.ProcessPath
        ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
    return CliRouter.Run(args, exePath, Console.Out);
}

var builder = Host.CreateApplicationBuilder(args);

// Core singletons (one automation context for the whole server in this phase).
builder.Services.AddSingleton<AutomationDispatcher>();
builder.Services.AddSingleton<WindowManager>();
builder.Services.AddSingleton<WindowTools>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
```
(Top-level statements allow a mix of `return <int>` and the final `await ... ; return 0;` —
the program's effective return type becomes `Task<int>`.)

- [ ] **Step 2: Verify the host still builds and the installer path works**

Run: `dotnet build src/FlaUI.Mcp.Server`
Expected: `Build succeeded`, 0 errors.
Then exercise the verb path without launching the host:
```bash
dotnet run --project src/FlaUI.Mcp.Server -- --version
dotnet run --project src/FlaUI.Mcp.Server -- print-config --agent generic
```
Expected: first prints `flaui-mcp <version>`; second prints a JSON snippet containing
`mcpServers` and `flaui-mcp`; both exit promptly (do NOT block on stdio).

- [ ] **Step 3: Verify no-args still starts the MCP host**

Run: `dotnet run --project src/FlaUI.Mcp.Server` then Ctrl+C after ~2s.
Expected: starts and waits on stdio (no crash). (Same behavior as before this task.)

- [ ] **Step 4: Commit**

```bash
git add src/FlaUI.Mcp.Server/Program.cs
git commit -m "feat(server): arg branch - installer verbs vs stdio host"
```

---

## Task 9: Inno Setup installer script

**Files:**
- Create: `installer/flaui-mcp.iss`

This script bundles the published `flaui-mcp.exe`, installs per-user, runs `install` post-install
and `uninstall` pre-uninstall, and stops a running instance before (re)install.

- [ ] **Step 1: Write the Inno Setup script**

Create `installer/flaui-mcp.iss`:
```pascal
; Inno Setup script for FlaUI.Mcp. Build with: ISCC.exe installer\flaui-mcp.iss
; Expects the published single-file exe at: publish\flaui-mcp.exe (see release CI / Task 11).
#define AppName "FlaUI.Mcp"
#define AppVersion "0.1.0"
#define ExeName "flaui-mcp.exe"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=FlaUI.Mcp
DefaultDirName={localappdata}\Programs\FlaUI.Mcp
DefaultGroupName=FlaUI.Mcp
PrivilegesRequired=lowest
OutputBaseFilename=flaui-mcp-setup
OutputDir=dist
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesEnvironment=yes

[Files]
Source: "..\publish\{#ExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "addtopath"; Description: "Add FlaUI.Mcp to PATH"; Flags: checkedonce

[Registry]
; Optional PATH addition (per-user) when the task is selected.
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata};{app}"; Tasks: addtopath; \
  Check: NeedsAddPath('{app}')

[Run]
; Configure every detected agent right after files are placed.
Filename: "{app}\{#ExeName}"; Parameters: "install --agent all"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Configuring agents..."

[UninstallRun]
; Revert agent config (targeted) before files are removed.
Filename: "{app}\{#ExeName}"; Parameters: "uninstall --agent all"; \
  Flags: runhidden waituntilterminated; RunOnceId: "FlauiMcpUnconfigure"

[Code]
function NeedsAddPath(Param: string): Boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Param + ';', ';' + OrigPath + ';') = 0;
end;

procedure StopRunningInstance();
var
  ResultCode: Integer;
begin
  // Stop a running server so the locked exe can be replaced (round-2: update file-lock).
  Exec('taskkill.exe', '/F /IM {#ExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningInstance();
  Result := '';
end;
```

- [ ] **Step 2: (Manual/documented) build the installer**

If Inno Setup is available locally (`ISCC.exe` on PATH, or install via
`winget install JRSoftware.InnoSetup`), validate the script compiles after a publish:
```bash
dotnet publish src/FlaUI.Mcp.Server -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
ISCC.exe installer/flaui-mcp.iss
```
Expected: `dist/flaui-mcp-setup.exe` is produced. If Inno Setup is not installed locally,
skip the local build — the release CI (Task 11) installs and runs it. Note "Inno build
skipped locally" in your report.

- [ ] **Step 3: Commit**

```bash
git add installer/flaui-mcp.iss
git commit -m "feat(dist): Inno Setup installer - per-user, configures agents, stops running exe"
```

---

## Task 10: Optional silent one-liner `dist/install.ps1`

**Files:**
- Create: `dist/install.ps1`

A thin wrapper that downloads the latest `flaui-mcp-setup.exe` from GitHub Releases and runs
it silently. `$Owner`/`$Repo` are placeholders the maintainer sets once the public repo exists
(the repo has no remote yet).

- [ ] **Step 1: Write the installer wrapper**

Create `dist/install.ps1`:
```powershell
<#
.SYNOPSIS  Download and silently run the FlaUI.Mcp installer.
.EXAMPLE   irm https://raw.githubusercontent.com/<owner>/<repo>/master/dist/install.ps1 | iex
#>
[CmdletBinding()]
param(
    [string] $Version = "latest",
    [string] $Owner   = "OWNER_PLACEHOLDER",   # set to the GitHub owner once the repo is published
    [string] $Repo    = "REPO_PLACEHOLDER"
)
$ErrorActionPreference = "Stop"

$api = if ($Version -eq "latest") {
    "https://api.github.com/repos/$Owner/$Repo/releases/latest"
} else {
    "https://api.github.com/repos/$Owner/$Repo/releases/tags/$Version"
}

Write-Host "Resolving FlaUI.Mcp release ($Version)..."
$release = Invoke-RestMethod -Uri $api -Headers @{ "User-Agent" = "flaui-mcp-installer" }
$asset = $release.assets | Where-Object { $_.name -eq "flaui-mcp-setup.exe" } | Select-Object -First 1
if (-not $asset) { throw "flaui-mcp-setup.exe not found in release '$($release.tag_name)'." }

$dest = Join-Path $env:TEMP "flaui-mcp-setup.exe"
Write-Host "Downloading $($asset.name) ($($release.tag_name))..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $dest -Headers @{ "User-Agent" = "flaui-mcp-installer" }

Write-Host "Running installer (silent)..."
$p = Start-Process -FilePath $dest -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Installer exited with code $($p.ExitCode)." }
Write-Host "FlaUI.Mcp installed. Restart agy if you use it, so it loads the new tools."
```

- [ ] **Step 2: Lint the script parses**

Run: `pwsh -NoProfile -Command "[scriptblock]::Create((Get-Content -Raw dist/install.ps1)) | Out-Null; 'parse ok'"`
Expected: prints `parse ok` (no parse errors). (It is NOT executed end-to-end here — there is
no release yet.)

- [ ] **Step 3: Commit**

```bash
git add dist/install.ps1
git commit -m "feat(dist): silent one-liner wrapper over the Inno installer"
```

---

## Task 11: Release CI workflow

**Files:**
- Create: `.github/workflows/release.yml`

- [ ] **Step 1: Write the workflow**

Create `.github/workflows/release.yml`:
```yaml
name: release
on:
  push:
    tags: [ "v*" ]

permissions:
  contents: write   # create releases + upload assets

jobs:
  build-and-release:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - name: Build
        run: dotnet build -c Release

      - name: Test (non-UIA only; runners are headless)
        run: dotnet test -c Release --filter "Category!=Desktop"

      - name: Publish self-contained single-file exe
        run: >
          dotnet publish src/FlaUI.Mcp.Server -c Release -r win-x64 --self-contained
          -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
          -o publish

      - name: Install Inno Setup
        run: choco install innosetup --no-progress -y

      - name: Build installer
        run: '& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\flaui-mcp.iss'

      - name: Checksums
        shell: pwsh
        run: |
          Get-FileHash dist\flaui-mcp-setup.exe -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  flaui-mcp-setup.exe" } | Out-File -Encoding ascii dist\SHA256SUMS.txt
          Get-FileHash publish\flaui-mcp.exe   -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  flaui-mcp.exe" }       | Out-File -Encoding ascii -Append dist\SHA256SUMS.txt

      - name: Create GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          files: |
            dist/flaui-mcp-setup.exe
            publish/flaui-mcp.exe
            dist/install.ps1
            dist/SHA256SUMS.txt
```

- [ ] **Step 2: Validate the YAML parses**

Run: `pwsh -NoProfile -Command "Get-Content -Raw .github/workflows/release.yml | Out-Null; 'read ok'"`
(Optionally, if `python` is available: `python -c "import yaml; yaml.safe_load(open('.github/workflows/release.yml')); print('yaml ok')"`.)
Expected: no error. (The workflow only runs in CI on a real tag; it is not executed here.)

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci: release workflow - build, non-UIA tests, publish exe + Inno installer, GitHub Release"
```

---

## Task 12: Full-suite green + phase wrap

- [ ] **Step 1: Run the entire suite (interactive desktop session)**

Run: `dotnet build test/FlaUI.Mcp.TestApp && dotnet test`
Expected: ALL pass. New non-UIA install tests add to Phase 1's 23: JsoncFile 3, McpServerEntry 1,
Generic 3, Agy 3, Claude 3, CliRouter (5 Theory rows + 2). Confirm `Failed: 0`. (Exact total
depends on how xUnit counts Theory rows; the gate is **0 failures**, not a specific number.)

- [ ] **Step 2: Confirm the CI filter selects a clean non-UIA subset**

Run: `dotnet test --filter "Category!=Desktop"`
Expected: `Failed: 0`, and none of WindowManager/WindowOperations/WindowTools run.

- [ ] **Step 3: (Optional, interactive) real round-trip against this machine's agents**

Back up `~/.gemini/*` first, then:
```bash
dotnet run --project src/FlaUI.Mcp.Server -- install --agent generic --config "$LOCALAPPDATA/flaui-smoke.json"
```
Expected: the file is created with `mcpServers.flaui-mcp`. Then
`-- uninstall --agent generic --config "$LOCALAPPDATA/flaui-smoke.json"` removes it. (Use the
`--config` override so you do NOT touch real agy/Claude config during the smoke.)

- [ ] **Step 4: Update execution-status memory**

Record distribution sub-project complete with the final commit SHA; set the resume point to
"publish repo to GitHub (set `$Owner`/`$Repo` in install.ps1) + cut the first `v0.1.0` tag to
exercise release CI; then verify agy config-file precedence on live agy (Task 7 STOP-and-verify)."

- [ ] **Step 5: Final commit (if any uncommitted docs)**

```bash
git add -A && git commit -m "docs: distribution sub-project complete" || echo "nothing to commit"
```

---

## Notes for the implementer

- **No real agents are touched by the unit tests** — every config-writer test uses temp files.
  Only the optional Task 12 Step 3 smoke and a real `dotnet run -- install` touch live configs;
  if you run those, back up `~/.gemini/*` first and prefer the `--config` override.
- **agy precedence is the one open item** (Task 7 STOP-and-verify). It is isolated to two
  default path strings; resolving it does not change tested logic.
- **Comments are not preserved** across a config save (System.Text.Json limitation) — the
  timestamped backup is the recovery path; tests assert key-preservation + backup, not comment
  survival. This is the spec's "JSONC-safe" softened to what stdlib can deliver.
- **The repo has no GitHub remote yet.** `release.yml` is self-referencing (`github.repository`)
  so it needs no slug, but `dist/install.ps1` has `OWNER_PLACEHOLDER`/`REPO_PLACEHOLDER` to set
  once the public repo exists (Task 12 resume point).
- **Do not change** the Phase 1 server behavior — the no-args path must remain the stdio host.
```
