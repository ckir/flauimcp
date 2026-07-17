# Collision-Marker Durability Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `CollisionMarker` persistence crash-safe so a corrupt, interrupted-write, or future-version marker can never cause silent data loss on `Record`, silent plugin-stranding on `Restore`, a false "nothing disabled" from `status`, or leaked `-1`/`-2` sentinels — and stop `Apply` blaming the user for a disable it performed earlier in the same run.

**Architecture:** Introduce one internal classifier — `ReadState` → `(MarkerState, entries)` — that never throws and is the single source of truth every caller branches on. `Read` becomes a thin `Present`-gated wrapper (contract preserved). `Record` writes atomically (tmp → `File.Move`) and backs up only a `Corrupt` file. `Restore` switches on state and sweeps stale `.bak-*` unconditionally. `status` describes corrupt/future markers instead of masking them. Two one-line fixes (sentinel translation, R5 `Concat`) round it out.

**Tech Stack:** C# (net10.0-windows), `System.Text.Json.Nodes`, xUnit. Same-assembly `InternalsVisibleTo("FlaUI.Mcp.Tests")` lets `internal` types be tested.

**HELD-RELEASE INVARIANT:** all work lands on branch `fix/distribution-live-defects`, **commit-only, one commit per task**. `master` stays `2ffd6a1` — **no push, no tag, no merge**. This is user-decided; do not re-litigate.

**Spec of record:** `docs/superpowers/specs/2026-07-16-collision-marker-hardening-design.md` (goals 1–7, panel ledger §"Panel ledger").

**Per-task gate (unless a step says otherwise):**
Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop"`
Expected: `Failed: 0` (the full non-Desktop suite stays green after every task).
Single-test runs use: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~<TestName>"`.

---

## File Structure

- `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs` — add `internal enum MarkerState`, `internal ReadState`, rewrite `Read` as a wrapper, rewrite `Record` (atomic write + `Corrupt`-only backup + `FutureVersion` refuse + `SameEntry` dedup + null-on-success), add `SweepBackups` + private helpers `BuildJson`/`WriteAtomically`/`DedupBySameEntry`/`BackUpCorrupt`/`AsString`. **Never throws** stays true.
- `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs` — `Restore` state-aware guard + `.bak` sweep in `finally`; add `DescribeExit` sentinel helper used in the re-enable-failure message; `Apply` R5 check consults `recorded.Concat(justDisabled)`.
- `src/FlaUI.Mcp.Server/Install/InstallStatus.cs` — `DescribeCollisions` switches on `ReadState` (Present output byte-identical to today).
- `test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs` — `ReadState` classification + `Record` behavior tests; extend the pinned corrupt theory.
- `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs` — `Corrupt`/`FutureVersion` guard + four-branch `.bak` sweep + sentinel-translation tests.
- `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs` — R5 `Concat` test.
- `test/FlaUI.Mcp.Tests/Install/InstallStatusTests.cs` — corrupt-marker + Present byte-identical status tests.

**Verified against code that exists now (2026-07-17):** `CollisionMarker.Record` @ `CollisionMarker.cs:53-84` (`File.WriteAllText` @ :73, `Directory.CreateDirectory` @ :72); `Read` @ :88-108; `SameEntry` @ :44-47; `Delete` @ :111-123; `PathIn` @ :36; `FileName` @ :34. `ClaudeCollisionRemedy.Apply` @ :51-146 (R5 check @ :117, promise-suppression @ :136-143); `Restore` @ :149-217 (`if (recorded.Count == 0) return null;` @ :152, re-enable failure `claude exited {r.Code}` @ :205, `TryReadInventory` sentinel mapping @ :240-245); `MarketplaceId` @ :32. `InstallStatus.DescribeCollisions` @ :63-73 (uses `CollisionMarker.Read` @ :65); `LogName` @ :19. `ProcessRunner.NotFound = -1` @ :18, `TimedOut = -2` @ :20. `JsoncFile.Save` atomic pattern @ :27-36. `InternalsVisibleTo("FlaUI.Mcp.Tests")` @ `src/FlaUI.Mcp.Server/Properties/AssemblyInfo.cs:3`.

**Pre-flight (do once, not a commit):**
Run: `git -C . rev-parse --abbrev-ref HEAD && git rev-parse --short master`
Expected: branch `fix/distribution-live-defects`; `master` == `2ffd6a1`. If not, STOP and report.

---

## Task 1: `MarkerState` + `ReadState` + safe `Read` wrapper

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs` (add enum + `ReadState`; replace `Read` body @ :88-108)
- Test: `test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs`

**Goal of this task:** classify the marker four ways and keep `Read` empty-on-anything-non-`Present`. No behavior change for any existing test.

- [ ] **Step 1: Write the failing tests**

Add these to `CollisionMarkerTests.cs` (after the existing tests, before the closing brace). They reference the not-yet-added `CollisionMarker.ReadState` and `MarkerState`:

```csharp
    private static string WriteMarker(string content)
    {
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), content);
        return s;
    }

    [Fact]
    public void ReadState_reports_Absent_when_no_file_exists()
        => Assert.Equal(MarkerState.Absent, CollisionMarker.ReadState(TempState()).State);

    [Fact]
    public void ReadState_reports_Present_for_a_valid_v1_marker()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { ProjA });

        var (state, entries) = CollisionMarker.ReadState(s);

        Assert.Equal(MarkerState.Present, state);
        Assert.Equal(ProjA, Assert.Single(entries));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json")]
    [InlineData("[]")]                                          // root not an object
    [InlineData("{ \"disabled\": [] }")]                        // version missing
    [InlineData("{ \"version\": \"1\", \"disabled\": [] }")]    // version not a number
    [InlineData("{ \"version\": 0, \"disabled\": [] }")]        // version < 1
    [InlineData("{ \"version\": 1 }")]                          // disabled missing
    [InlineData("{ \"version\": 1, \"disabled\": \"x\" }")]     // disabled not an array
    public void ReadState_reports_Corrupt_for_structural_failures(string content)
    {
        var (state, entries) = CollisionMarker.ReadState(WriteMarker(content));
        Assert.Equal(MarkerState.Corrupt, state);
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData("{ \"version\": 2, \"disabled\": [] }")]
    [InlineData("{ \"version\": 999 }")]          // no disabled key — still FutureVersion (honored on version alone)
    [InlineData("{ \"version\": 2.1, \"disabled\": [] }")]   // fractional must not throw into Corrupt (double parse)
    public void ReadState_reports_FutureVersion_for_a_version_greater_than_one(string content)
    {
        var (state, entries) = CollisionMarker.ReadState(WriteMarker(content));
        Assert.Equal(MarkerState.FutureVersion, state);
        Assert.Empty(entries);
    }

    [Fact]
    public void ReadState_drops_only_the_malformed_entry_and_keeps_valid_siblings()
    {
        // id is a NUMBER (a bare (string?) cast would THROW, not return null), scope blank, projectPath
        // wrong-typed — each drops that entry only; the valid sibling survives.
        var s = WriteMarker("""
        {
          "version": 1,
          "disabled": [
            { "id": 123, "scope": "user" },
            { "id": "flaui-mcp@flaui-mcp", "scope": "" },
            { "id": "flaui-mcp@flaui-mcp", "scope": "local", "projectPath": 7 },
            { "id": "flaui-mcp@flaui-mcp", "scope": "user", "projectPath": null }
          ]
        }
        """);

        var (state, entries) = CollisionMarker.ReadState(s);

        Assert.Equal(MarkerState.Present, state);
        Assert.Equal(new DisabledEntry("flaui-mcp@flaui-mcp", "user", null), Assert.Single(entries));
    }

    [Fact]
    public void ReadState_yields_Present_empty_when_every_entry_is_dropped()
    {
        var (state, entries) = CollisionMarker.ReadState(
            WriteMarker("""{ "version": 1, "disabled": [ { "id": 1 } ] }"""));
        Assert.Equal(MarkerState.Present, state);
        Assert.Empty(entries);
    }

    [Fact]
    public void Read_is_empty_for_a_future_version_marker()
        => Assert.Empty(CollisionMarker.Read(
            WriteMarker("""{ "version": 2, "disabled": [ { "id": "x", "scope": "user" } ] }""")));
```

Also extend the existing pinned theory `A_corrupt_marker_reads_as_empty_and_never_throws` (@ :110-121) so the `Read` wrapper is pinned empty for a future-version marker too (per spec Testing §). Add this `InlineData` line to that theory's attribute list:

```csharp
    [InlineData("{ \"version\": 2, \"disabled\": [] }")]   // FutureVersion — Read still collapses it to empty
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~CollisionMarkerTests"`
Expected: FAIL — compile error, `CollisionMarker` has no `ReadState` and `MarkerState` is undefined.

- [ ] **Step 3: Add `MarkerState` and `ReadState`, and rewrite `Read`**

In `CollisionMarker.cs`, add the enum as a **top-level** type in the namespace, immediately after the `DisabledEntry` record (@ :11) and before the `CollisionMarker` class `<summary>`. It MUST be top-level (not nested in the class): every reference in `ClaudeCollisionRemedy`, `InstallStatus`, and the tests uses the unqualified name `MarkerState.X`, which only resolves if the enum is a sibling of `DisabledEntry` in the namespace. Nesting it inside `CollisionMarker` would force `CollisionMarker.MarkerState.X` everywhere and break compilation across all three files.

```csharp
/// <summary>How <see cref="CollisionMarker.ReadState"/> classified the marker file.
/// Absent = no file. Corrupt = present but structurally unreadable (fail-safe: collapse to empty).
/// FutureVersion = written by a newer build (version &gt; 1) — leave it untouched. Present = valid v1.</summary>
internal enum MarkerState { Absent, Corrupt, FutureVersion, Present }
```

(`internal` suffices — `InstallStatus` is in the same assembly and the test project has `InternalsVisibleTo("FlaUI.Mcp.Tests")` @ `src/FlaUI.Mcp.Server/Properties/AssemblyInfo.cs:3`.)

Add `ReadState` and the `AsString` helper (place `ReadState` just above the current `Read` method):

```csharp
    /// <summary>Classify the marker and project its entries. The single source of truth Read, Record,
    /// Restore, and status all branch on. NEVER throws — every failure collapses to Corrupt.</summary>
    internal static (MarkerState State, IReadOnlyList<DisabledEntry> Entries) ReadState(string stateDir)
    {
        var empty = (IReadOnlyList<DisabledEntry>)System.Array.Empty<DisabledEntry>();
        try
        {
            var path = PathIn(stateDir);
            if (!File.Exists(path)) return (MarkerState.Absent, empty);
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return (MarkerState.Corrupt, empty);

            // version must be a NUMBER >= 1. Read as double so a large integer or a fractional future
            // version (2.1) neither overflows nor throws into Corrupt.
            if (o["version"] is not JsonValue vNode || vNode.GetValueKind() != JsonValueKind.Number)
                return (MarkerState.Corrupt, empty);
            var version = vNode.GetValue<double>();
            if (version < 1) return (MarkerState.Corrupt, empty);
            if (version > 1) return (MarkerState.FutureVersion, empty);   // honored on version alone (goal 4)

            if (o["disabled"] is not JsonArray arr) return (MarkerState.Corrupt, empty);

            var list = new List<DisabledEntry>();
            foreach (var node in arr)
            {
                if (node is not JsonObject e) continue;
                // A bare (string?)e["id"] cast THROWS on a numeric/boolean node (it does NOT return
                // null), so every field is gated on GetValueKind == String; a wrong-typed field drops
                // THIS entry only, never the whole file.
                var id = AsString(e["id"]);
                var scope = AsString(e["scope"]);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(scope)) continue;

                // projectPath may be legitimately absent or JSON null (user scope). A present-but-
                // wrong-typed projectPath drops the entry.
                var ppNode = e["projectPath"];
                string? projectPath;
                if (ppNode is null) projectPath = null;                     // absent OR JSON null
                else { projectPath = AsString(ppNode); if (projectPath is null) continue; }

                list.Add(new DisabledEntry(id!, scope!, projectPath));
            }
            return (MarkerState.Present, list);
        }
        catch { return (MarkerState.Corrupt, empty); }
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
```

Replace the entire existing `Read` method body (@ :88-108) with the wrapper:

```csharp
    /// <summary>Entries we recorded. Empty for every non-Present state (Absent/Corrupt/FutureVersion) —
    /// the fail-safe direction: an unreadable or future marker must mean "restore nothing", never
    /// "enable things we have no record of". Never throws.</summary>
    public static IReadOnlyList<DisabledEntry> Read(string stateDir)
    {
        var (state, entries) = ReadState(stateDir);
        return state == MarkerState.Present ? entries : System.Array.Empty<DisabledEntry>();
    }
```

(`Record` still calls `Read(stateDir).ToList()` at this point — that keeps compiling; Task 2 rewrites `Record`.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~CollisionMarkerTests"`
Expected: PASS (all new + all existing `CollisionMarkerTests`).

- [ ] **Step 5: Run the full non-Desktop suite**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop"`
Expected: `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CollisionMarker.cs test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs
git commit -m "fix(install): classify marker state via ReadState (Absent/Corrupt/FutureVersion/Present)"
```

---

## Task 2: `Record` — tailored atomic write, back up only a `Corrupt` file

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs` (rewrite `Record` @ :53-84; add helpers)
- Test: `test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs`
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs` (the one Apply-integration test)

**Goal of this task:** atomic write (goal 3); `Corrupt` → preserve old bytes to a `.bak`, start fresh, **return null** so `Apply`'s promise still fires (goals 1, spec Critical); `FutureVersion` → refuse + non-null warning (goal 4); dedup by `SameEntry`.

- [ ] **Step 1: Write the failing tests**

Add to `CollisionMarkerTests.cs`:

```csharp
    [Fact]
    public void Record_over_a_corrupt_marker_preserves_the_old_bytes_and_still_records()
    {
        var s = TempState();
        var path = CollisionMarker.PathIn(s);
        File.WriteAllText(path, "{ torn half-written garbage");   // a Corrupt file

        var warning = CollisionMarker.Record(s, new[] { UserEntry });

        Assert.Null(warning);                                      // recovery succeeded => promise holds
        Assert.Equal(UserEntry, Assert.Single(CollisionMarker.Read(s)));   // fresh marker written
        var baks = Directory.GetFiles(s, CollisionMarker.FileName + ".bak-*");
        Assert.Single(baks);
        Assert.Equal("{ torn half-written garbage", File.ReadAllText(baks[0]));   // old bytes preserved
    }

    [Fact]
    public void Record_refuses_to_touch_a_future_version_marker()
    {
        var s = TempState();
        var path = CollisionMarker.PathIn(s);
        var future = """{ "version": 2, "disabled": [ { "id": "keep@me", "scope": "user" } ] }""";
        File.WriteAllText(path, future);

        var warning = CollisionMarker.Record(s, new[] { UserEntry });

        Assert.NotNull(warning);
        Assert.Contains("newer flaui-mcp", warning);
        Assert.Equal(future, File.ReadAllText(path));               // byte-for-byte unchanged
        Assert.Empty(Directory.GetFiles(s, CollisionMarker.FileName + ".bak-*"));
    }

    [Fact]
    public void Record_on_the_happy_path_writes_no_backup()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { ProjA });   // Absent -> write
        CollisionMarker.Record(s, new[] { ProjB });   // Present -> merge, no .bak
        Assert.Empty(Directory.GetFiles(s, CollisionMarker.FileName + ".bak-*"));
        Assert.Equal(2, CollisionMarker.Read(s).Count);
    }

    [Fact]
    public void Record_discards_an_oversized_corrupt_file_without_a_backup_and_still_records()
    {
        var s = TempState();
        var path = CollisionMarker.PathIn(s);
        File.WriteAllText(path, new string('x', (int)CollisionMarker.BackupSizeCap + 1));   // > 1 MB garbage

        var warning = CollisionMarker.Record(s, new[] { UserEntry });

        Assert.Null(warning);
        Assert.Equal(UserEntry, Assert.Single(CollisionMarker.Read(s)));
        Assert.Empty(Directory.GetFiles(s, CollisionMarker.FileName + ".bak-*"));   // oversized => discarded
    }

    [Fact]
    public void Record_dedups_a_case_variant_duplicate_in_a_present_baseline()
    {
        var s = TempState();
        // Hand-write a Present marker with a duplicate that differs only in path casing.
        File.WriteAllText(CollisionMarker.PathIn(s), """
        {
          "version": 1,
          "disabled": [
            { "id": "flaui-mcp@flaui-mcp", "scope": "local", "projectPath": "C:\\Proj" },
            { "id": "flaui-mcp@flaui-mcp", "scope": "local", "projectPath": "c:\\proj" }
          ]
        }
        """);

        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "local", @"C:\other") });

        // baseline deduped to one, plus the new entry = 2 (not 3).
        Assert.Equal(2, CollisionMarker.Read(s).Count);
    }

    // Spec goal 3 partial: a torn/interrupted write leaves a `.tmp`; ReadState reads only the `.json`,
    // so the sibling is ignored and the next Record is unaffected.
    [Fact]
    public void A_stale_tmp_sibling_is_ignored_and_does_not_corrupt_the_next_record()
    {
        var s = TempState();
        CollisionMarker.Record(s, new[] { ProjA });
        File.WriteAllText(CollisionMarker.PathIn(s) + ".tmp", "{ half-written interrupted garbage");

        var (state, entries) = CollisionMarker.ReadState(s);
        Assert.Equal(MarkerState.Present, state);
        Assert.Equal(ProjA, Assert.Single(entries));

        Assert.Null(CollisionMarker.Record(s, new[] { ProjB }));   // next record unaffected
        Assert.Equal(2, CollisionMarker.Read(s).Count);
    }
```

Also add this **Apply-integration** test to `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs` (it needs the `FakeClaude` fake defined there). It guards the Critical round-2 AB invariant — a Corrupt marker that `Record` *recovers* returns null, so `Apply` must still emit the re-enable promise:

```csharp
    // Round-2 AB (Critical): when Record RECOVERS from a corrupt marker it returns null (it DID record),
    // so Apply must still PROMISE the re-enable. The promise is suppressed only on a genuine record
    // failure — a corrupt-but-recovered marker is not one.
    [Fact]
    public void A_corrupt_marker_recovered_during_apply_still_promises_the_re_enable()
    {
        var cli = new FakeClaude().Install("flaui-mcp@flaui-mcp", "user", null, enabled: true);
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), "{ torn half-written");   // a Corrupt marker is present

        var warning = Remedy(cli, s).Apply();

        Assert.NotNull(warning);
        Assert.Contains("they will be re-enabled if you uninstall", warning);
        Assert.Equal(new DisabledEntry("flaui-mcp@flaui-mcp", "user", null), Assert.Single(CollisionMarker.Read(s)));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~CollisionMarkerTests|FullyQualifiedName~ClaudeCollisionRemedyTests"`
Expected: FAIL — compile error (`CollisionMarker.BackupSizeCap` undefined) and, once that resolves in Step 3, the new behavior assertions.

- [ ] **Step 3: Rewrite `Record` and add helpers**

Replace the entire existing `Record` method (@ :53-84, including its `<summary>`) with:

```csharp
    /// <summary>Size above which a corrupt marker is treated as non-marker garbage and discarded
    /// (overwritten) rather than backed up. A real marker is a few small JSON objects.</summary>
    public const long BackupSizeCap = 1024 * 1024;   // 1 MB

    /// <summary>Merge these entries in, preserving any already recorded, and write atomically.
    /// Returns null on success (INCLUDING recovery from a Corrupt file — it DID record, so the caller's
    /// re-enable promise must still fire). Returns a non-null reason only when the record did NOT
    /// happen: a genuine write failure, or a refusal to touch a FutureVersion marker.</summary>
    public static string? Record(string stateDir, IReadOnlyList<DisabledEntry> entries)
    {
        if (entries.Count == 0) return null;   // never write an empty marker: it would fire a no-op restore

        var (state, existing) = ReadState(stateDir);
        if (state == MarkerState.FutureVersion)
            return $"the restore record at {PathIn(stateDir)} was written by a newer flaui-mcp and was " +
                   "left unchanged; this install's disable was NOT recorded. If you did not expect this, " +
                   $"remove {PathIn(stateDir)}.";

        try
        {
            IReadOnlyList<DisabledEntry> baseline;
            if (state == MarkerState.Corrupt)
            {
                BackUpCorrupt(stateDir);                       // best-effort: preserve forensic bytes
                baseline = System.Array.Empty<DisabledEntry>();
            }
            else
            {
                baseline = state == MarkerState.Present
                    ? DedupBySameEntry(existing)
                    : System.Array.Empty<DisabledEntry>();     // Absent
            }

            var merged = baseline.ToList();
            foreach (var e in entries)
                if (!merged.Any(m => SameEntry(m, e))) merged.Add(e);

            WriteAtomically(stateDir, BuildJson(merged));
            return null;                                        // recorded (incl. Corrupt recovery)
        }
        catch (Exception e)
        {
            // NOT swallowed. We have already disabled the user's plugin; if the record does not survive,
            // uninstall can never put it back. The caller turns this into a Warning the user can see.
            return $"disabled a conflicting plugin but could NOT record it at {PathIn(stateDir)} ({e.Message}) — " +
                   "uninstalling flaui-mcp will not re-enable it automatically.";
        }
    }

    private static JsonObject BuildJson(IReadOnlyList<DisabledEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
            arr.Add(new JsonObject { ["id"] = e.Id, ["scope"] = e.Scope, ["projectPath"] = e.ProjectPath });
        return new JsonObject { ["version"] = 1, ["disabled"] = arr };
    }

    // Follows JsoncFile.Save's tmp -> File.Move pattern (the repo's atomic-write convention), but with
    // NO happy-path backup: the merged marker is a superset of the old, so an atomic replace loses
    // nothing and leaves no .bak litter in the state dir.
    private static void WriteAtomically(string stateDir, JsonObject root)
    {
        Directory.CreateDirectory(stateDir);
        var path = PathIn(stateDir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);   // atomic directory-entry replace on the same volume
    }

    private static IReadOnlyList<DisabledEntry> DedupBySameEntry(IReadOnlyList<DisabledEntry> entries)
    {
        var result = new List<DisabledEntry>();
        foreach (var e in entries)
            if (!result.Any(m => SameEntry(m, e))) result.Add(e);   // SameEntry, NOT record .Distinct()
        return result;
    }

    // Move a corrupt marker aside to a collision-free .bak so its bytes survive for forensics, unless it
    // is absurdly large (a sign of non-marker garbage — discarded by the coming overwrite instead).
    // Best-effort: a backup hiccup must not fail an otherwise-recoverable record.
    private static void BackUpCorrupt(string stateDir)
    {
        try
        {
            var path = PathIn(stateDir);
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > BackupSizeCap) return;   // oversized garbage: leave it, overwrite discards it
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var dest = $"{path}.bak-{stamp}";
            for (var n = 0; File.Exists(dest); n++) dest = $"{path}.bak-{stamp}-{n}";   // same-ms collision-free
            File.Move(path, dest);
        }
        catch { /* best-effort: a failed backup must not fail a recoverable record */ }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~CollisionMarkerTests|FullyQualifiedName~ClaudeCollisionRemedyTests"`
Expected: PASS (new tests — incl. the Apply corrupt-recovery promise test — plus all existing `Record`/round-trip/dedup/Apply tests stay green).

- [ ] **Step 5: Run the full non-Desktop suite**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop"`
Expected: `Failed: 0` (this also re-runs the `ClaudeCollisionRemedyTests` that call `Record` indirectly — the merge, reinstall-untouched, and promise-suppression tests must stay green).

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CollisionMarker.cs test/FlaUI.Mcp.Tests/Install/CollisionMarkerTests.cs test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs
git commit -m "fix(install): atomic marker write; preserve a corrupt marker instead of clobbering"
```

---

## Task 3: `Restore` — state-aware guard + unconditional `.bak` sweep

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/CollisionMarker.cs` (add `SweepBackups`)
- Modify: `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs` (rewrite `Restore` opening @ :149-153; wrap body in `try/finally`)
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs`

**Goal of this task:** `Corrupt`/`FutureVersion` at uninstall → warn (+ recourse for `Corrupt`), keep the marker, never silent success (goal 2); the `.bak` sweep runs on **all four** branches (flow-control mandate — sweep in a `finally`).

- [ ] **Step 1: Write the failing tests**

Add to `ClaudeCollisionRestoreTests.cs`:

```csharp
    [Fact]
    public void A_corrupt_marker_at_uninstall_warns_with_recourse_and_keeps_the_file()
    {
        var cli = new FakeCli();
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), "{ torn half-written");   // Corrupt

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.NotNull(warning);
        Assert.Contains("unreadable", warning);
        Assert.Contains("claude plugin list", warning);
        Assert.Contains(ClaudeCollisionRemedy.MarketplaceId, warning);
        Assert.Empty(cli.Calls);                                   // never tried to enable
        Assert.True(File.Exists(CollisionMarker.PathIn(s)), "a corrupt marker must be kept, not silently deleted");
    }

    [Fact]
    public void A_future_version_marker_at_uninstall_warns_and_keeps_the_file()
    {
        var cli = new FakeCli();
        var s = TempState();
        File.WriteAllText(CollisionMarker.PathIn(s), """{ "version": 2, "disabled": [] }""");

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.NotNull(warning);
        Assert.Contains("newer flaui-mcp", warning);
        Assert.Empty(cli.Calls);
        Assert.True(File.Exists(CollisionMarker.PathIn(s)));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("corrupt")]
    [InlineData("future")]
    [InlineData("present")]
    public void The_bak_sweep_runs_on_every_restore_branch(string kind)
    {
        var cli = new FakeCli { ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""" };
        var s = TempState();
        var stale = CollisionMarker.PathIn(s) + ".bak-20200101000000";
        File.WriteAllText(stale, "old");

        switch (kind)
        {
            case "absent":  break;                                                             // no marker file
            case "corrupt": File.WriteAllText(CollisionMarker.PathIn(s), "{ torn"); break;
            case "future":  File.WriteAllText(CollisionMarker.PathIn(s), """{ "version": 2, "disabled": [] }"""); break;
            case "present": CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) }); break;
        }

        new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.False(File.Exists(stale), $"the stale .bak must be swept on the '{kind}' branch");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~ClaudeCollisionRestoreTests"`
Expected: FAIL — the corrupt/future markers currently read as empty → old `Restore` returns null (no warning, file leaks), and no sweep exists.

- [ ] **Step 3: Add `SweepBackups` to `CollisionMarker`**

Add this method to `CollisionMarker.cs` (e.g. after `Delete`):

```csharp
    /// <summary>Best-effort delete of THIS marker's own stale .bak-* files in the state dir. Runs on
    /// every uninstall regardless of the active marker's state. Per-file try/catch so an AV-locked
    /// backup never fails the uninstall. Never throws.</summary>
    public static void SweepBackups(string stateDir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(stateDir, FileName + ".bak-*"))
                try { File.Delete(f); } catch { /* leave a locked backup; never fail uninstall */ }
        }
        catch { /* state dir gone or unreadable: nothing to sweep */ }
    }
```

- [ ] **Step 4: Rewrite `Restore`'s guard and wrap the body in `try/finally`**

In `ClaudeCollisionRemedy.cs`, replace the **entire** `Restore` method (@ :148-217, from its `<summary>` through the closing brace) with the version below. The body from `var warnings = new List<string>();` down through the final `return` is character-for-character today's code (the `TryReadInventory` recourse block, the per-entry `foreach` restore loop, the `CollisionMarker.Delete` consume) — the only changes are: (a) the opening `Read`+count-guard is replaced by the `ReadState` switch; (b) the whole thing is wrapped in `try { … } finally { CollisionMarker.SweepBackups(_stateDir); }` so the sweep runs on every path, including early returns and exceptions (flow-control mandate). The enable-failure line still reads `claude exited {r.Code}` here — Task 5 edits that line afterward.

```csharp
    /// <summary>Uninstall side: put back exactly what we disabled, then consume the marker (R7).</summary>
    public string? Restore()
    {
        try
        {
            var (state, recorded) = CollisionMarker.ReadState(_stateDir);

            if (state == MarkerState.Absent) return null;
            if (state == MarkerState.FutureVersion)
                return $"the restore record at {CollisionMarker.PathIn(_stateDir)} was written by a newer " +
                       "flaui-mcp; it was left in place and not acted on.";
            if (state == MarkerState.Corrupt)
                return $"the restore record at {CollisionMarker.PathIn(_stateDir)} is unreadable, so any " +
                       "conflicting plugin(s) we disabled may still be disabled and could not be re-enabled " +
                       "automatically. To check: run `claude plugin list`, and re-enable " +
                       $"{MarketplaceId} wherever it is disabled.";
            if (recorded.Count == 0) return null;   // Present, but every entry was dropped as malformed

            var warnings = new List<string>();

            // If we cannot read the inventory at all (e.g. the claude CLI is gone), we cannot restore —
            // and we must NOT consume the marker. It is still an ACCURATE record of a plugin we disabled
            // and have not put back; deleting it here would strand the user's plugin disabled with no
            // record anywhere that we were the ones who did it. R7's stale-marker hazard is about a
            // marker surviving a SUCCESSFUL consume, which this is not.
            if (!TryReadInventory(out var entries, out var listWarning))
            {
                // Manual recourse, but only for entries we could actually act on: an entry whose project
                // directory is gone is moot AND a `cd` into a deleted path is impossible, so listing it
                // would resurrect the same bad-recourse defect the in-loop guard below fixes. (agy panel
                // round 2 — the early return bypassed that guard.)
                var recoverable = recorded.Where(e => e.ProjectPath is null || _dirExists(e.ProjectPath)).ToList();
                var recourse = recoverable.Count == 0
                    ? "No manual action is possible (the recorded projects no longer exist)."
                    : "To restore manually: " + string.Join("; ", recoverable.Select(e =>
                        $"claude plugin enable {e.Id} --scope {e.Scope}" +
                        (e.ProjectPath is null ? "" : $" (run from {e.ProjectPath})")));
                return $"{listWarning} Your conflicting plugin(s) are still disabled and were NOT re-enabled. " +
                       "The record is kept at " + CollisionMarker.PathIn(_stateDir) + ". " + recourse;
            }

            var present = ClaudePluginInventory.Matching(entries, MarketplaceId);

            foreach (var e in recorded)
            {
                // Symmetric to Apply's guard: a project deleted AFTER we disabled the copy but BEFORE
                // uninstall cannot load the plugin (the collision is moot) and `enable` has nowhere valid
                // to run from. MEASURED: Process.Start with a missing working directory throws
                // Win32Exception, which ProcessRunner surfaces as a failed run — so without this guard we
                // would fall through to the failure branch below and print an impossible "run it from
                // <deleted path>" recourse. The `present` check does NOT catch this: the inventory can
                // still LIST a stale row for a deleted project (measured).
                if (e.ProjectPath is not null && !_dirExists(e.ProjectPath))
                {
                    warnings.Add($"{e.Id} ({Where(e)}) — its project directory no longer exists, so there was nothing to re-enable.");
                    continue;
                }

                // R2: the user may have uninstalled it themselves after we disabled it. Enabling a
                // plugin that no longer exists writes a phantom {id:true} (measured: enable succeeds for a
                // nonexistent id) — check the id is still installed first.
                if (!present.Any(p => Same(p, e)))
                {
                    warnings.Add($"{e.Id} ({Where(e)}) is no longer installed, so it was not re-enabled.");
                    continue;
                }

                var r = _run("claude", new[] { "plugin", "enable", e.Id, "--scope", e.Scope }, e.ProjectPath);
                if (r.Code != 0)
                    warnings.Add($"could not re-enable {e.Id} ({Where(e)}): claude exited {r.Code}. " +
                                 $"To restore it yourself: claude plugin enable {e.Id} --scope {e.Scope}" +
                                 (e.ProjectPath is null ? "" : $" (run it from {e.ProjectPath})"));
            }

            // R7: deleting the marker is part of consuming it. Delete even when a restore failed — a
            // surviving marker would later re-enable a plugin the user had deliberately disabled, which
            // is the exact outcome R1 exists to prevent. The failure is reported instead.
            var deleteWarning = CollisionMarker.Delete(_stateDir);
            if (deleteWarning is not null) warnings.Add(deleteWarning);

            return warnings.Count == 0 ? null : string.Join(" ", warnings);
        }
        finally
        {
            CollisionMarker.SweepBackups(_stateDir);
        }
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~ClaudeCollisionRestoreTests"`
Expected: PASS (new corrupt/future/sweep tests + all existing Restore tests, including the CLI-missing keep-marker, deleted-project, and full-round-trip cases).

- [ ] **Step 6: Run the full non-Desktop suite**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop"`
Expected: `Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/CollisionMarker.cs src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs
git commit -m "fix(install): state-aware Restore guard + unconditional .bak sweep"
```

---

## Task 4: `status` reads state, not just entries

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/InstallStatus.cs` (rewrite `DescribeCollisions` @ :63-73)
- Test: `test/FlaUI.Mcp.Tests/Install/InstallStatusTests.cs`

**Goal of this task:** `status` reports a corrupt/future marker instead of masking it as "nothing disabled" (goal 7); the `Present` output stays byte-identical to today's.

- [ ] **Step 1: Write the failing tests**

Add to `InstallStatusTests.cs`:

```csharp
    [Fact]
    public void Status_surfaces_a_corrupt_collision_record_instead_of_masking_it()
    {
        var (plugins, dataDir, claude, state) = TempPaths();
        Directory.CreateDirectory(state);
        File.WriteAllText(CollisionMarker.PathIn(state), "{ torn half-written");

        var s = InstallStatus.Describe(@"C:\flaui-mcp.exe", plugins, dataDir, claude, state);

        Assert.Contains("unreadable", s);
    }

    [Fact]
    public void Status_surfaces_a_future_version_collision_record()
    {
        var (plugins, dataDir, claude, state) = TempPaths();
        Directory.CreateDirectory(state);
        File.WriteAllText(CollisionMarker.PathIn(state), """{ "version": 2, "disabled": [] }""");

        var s = InstallStatus.Describe(@"C:\flaui-mcp.exe", plugins, dataDir, claude, state);

        Assert.Contains("newer flaui-mcp", s);
    }

    [Fact]
    public void Status_lists_a_present_collision_record_with_the_exact_existing_wording()
    {
        var (plugins, dataDir, claude, state) = TempPaths();
        CollisionMarker.Record(state, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var s = InstallStatus.Describe(@"C:\flaui-mcp.exe", plugins, dataDir, claude, state);

        // Byte-identical to today's output (round-4 Regression): header + entry line unchanged.
        Assert.Contains("Conflicting plugins we disabled (they will be re-enabled if you uninstall flaui-mcp):", s);
        Assert.Contains("  flaui-mcp@flaui-mcp — scope user", s);
    }

    [Fact]
    public void Status_says_nothing_about_collisions_when_the_record_is_absent()
    {
        var (plugins, dataDir, claude, state) = TempPaths();

        var s = InstallStatus.Describe(@"C:\flaui-mcp.exe", plugins, dataDir, claude, state);

        Assert.DoesNotContain("Conflicting", s);
        Assert.DoesNotContain("unreadable", s);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~InstallStatusTests"`
Expected: FAIL — the corrupt/future assertions fail (today `DescribeCollisions` reads empty and returns null, so `status` says nothing).

- [ ] **Step 3: Rewrite `DescribeCollisions`**

Replace the entire `DescribeCollisions` method (@ :63-73, including its `<summary>`) with:

```csharp
    /// <summary>The R5 channel: a plugin we disabled on the user's behalf is otherwise invisible —
    /// Setup ran hidden, so this is where they can find out. A corrupt or future-version record is
    /// surfaced rather than masked as "nothing disabled" (goal 7).</summary>
    private static string? DescribeCollisions(string stateDir)
    {
        var (state, recorded) = CollisionMarker.ReadState(stateDir);
        switch (state)
        {
            case MarkerState.Absent:
                return null;
            case MarkerState.Corrupt:
                return "Conflicting-plugin record: a restore record exists but is unreadable — re-enable " +
                       $"{ClaudeCollisionRemedy.MarketplaceId} manually if a driving-skill copy is still disabled.";
            case MarkerState.FutureVersion:
                return "Conflicting-plugin record: a restore record written by a newer flaui-mcp is present.";
            default:   // Present
                if (recorded.Count == 0) return null;
                var sb = new StringBuilder();
                sb.AppendLine("Conflicting plugins we disabled (they will be re-enabled if you uninstall flaui-mcp):");
                foreach (var e in recorded)
                    sb.AppendLine($"  {e.Id} — scope {e.Scope}{(e.ProjectPath is null ? "" : $" in {e.ProjectPath}")}");
                return sb.ToString().TrimEnd();
        }
    }
```

(The `Present` branch is character-for-character the old body, so every existing status test that exercises a Present marker keeps its exact output. `ClaudeCollisionRemedy.MarketplaceId` is a public const in the same namespace; no new `using` needed.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~InstallStatusTests"`
Expected: PASS (new + all existing `InstallStatusTests`, incl. the `status`-verb round-trip and print-config-stays-JSON tests).

- [ ] **Step 5: Run the full non-Desktop suite**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop"`
Expected: `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/InstallStatus.cs test/FlaUI.Mcp.Tests/Install/InstallStatusTests.cs
git commit -m "fix(install): status surfaces a corrupt/future marker instead of masking it"
```

---

## Task 5: Sentinel translation in `Restore`'s re-enable failure

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs` (add `DescribeExit`; use it @ :205)
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs`

**Goal of this task:** never print `claude exited -1` / `claude exited -2` to a human (goal 5). For a real OS exit code the wording is unchanged (`exited 1`), so existing tests stay green.

- [ ] **Step 1: Write the failing tests**

Add to `ClaudeCollisionRestoreTests.cs`:

```csharp
    [Fact]
    public void A_timed_out_re_enable_is_reported_as_timed_out_not_minus_two()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""",
            EnableCode = ProcessRunner.TimedOut,
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.NotNull(warning);
        Assert.Contains("timed out", warning);
        Assert.DoesNotContain("-2", warning);
    }

    [Fact]
    public void A_not_found_re_enable_is_reported_as_could_not_be_run_not_minus_one()
    {
        var cli = new FakeCli
        {
            ListJson = """[ { "id": "flaui-mcp@flaui-mcp", "scope": "user", "enabled": false } ]""",
            EnableCode = ProcessRunner.NotFound,
        };
        var s = TempState();
        CollisionMarker.Record(s, new[] { new DisabledEntry("flaui-mcp@flaui-mcp", "user", null) });

        var warning = new ClaudeCollisionRemedy(cli.Run, s).Restore();

        Assert.NotNull(warning);
        Assert.Contains("could not be run", warning);
        Assert.DoesNotContain("-1", warning);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~ClaudeCollisionRestoreTests"`
Expected: FAIL — today the message is `claude exited -2` / `claude exited -1`.

- [ ] **Step 3: Add `DescribeExit` and use it in the re-enable-failure message**

Add this static helper to `ClaudeCollisionRemedy.cs` (near the other private helpers at the bottom, e.g. beside `Where`):

```csharp
    // The internal -1/-2 sentinels (ProcessRunner.NotFound/TimedOut) are NOT OS exit codes; translate
    // them to plain words rather than leak them to a human, matching TryReadInventory's mapping.
    private static string DescribeExit(int code) => code switch
    {
        ProcessRunner.NotFound => "could not be run",
        ProcessRunner.TimedOut => "timed out",
        _ => $"exited {code}",
    };
```

Change the re-enable-failure warning (@ :205) from:

```csharp
                warnings.Add($"could not re-enable {e.Id} ({Where(e)}): claude exited {r.Code}. " +
```

to:

```csharp
                warnings.Add($"could not re-enable {e.Id} ({Where(e)}): claude {DescribeExit(r.Code)}. " +
```

(The rest of that statement — the `To restore it yourself: ...` recourse — is unchanged. For `r.Code == 1` this yields `claude exited 1`, identical to before, so `A_failed_enable_is_reported_with_a_manual_recourse_and_never_throws` stays green.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~ClaudeCollisionRestoreTests"`
Expected: PASS.

- [ ] **Step 5: Run the full non-Desktop suite**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop"`
Expected: `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRestoreTests.cs
git commit -m "fix(install): translate -1/-2 sentinels in Restore's re-enable failure message"
```

---

## Task 6: R5 must not blame the user for a disable earlier in the same `Apply` run

**Files:**
- Modify: `src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs` (R5 check @ :117)
- Test: `test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs`

**Goal of this task:** the "already disabled and we have no record — assuming you did" warning must consider entries we disabled earlier in THIS run, not just the pre-existing marker (goal 6). Otherwise a second CLI row for the same entry (e.g. a case-variant path) misattributes our own disable to the user.

- [ ] **Step 1: Write the failing test**

Add to `ClaudeCollisionRemedyTests.cs`:

```csharp
    // R5 (goal 6): two list rows resolve to the SAME entry (differ only in path casing, which the fake
    // treats as one target). We disable it via the first row; the second row's disable is then a no-op
    // exit 1 and the re-read shows it off. Without Concat(justDisabled) the marker (still empty at this
    // point) does not contain it, so it is wrongly blamed on the user. With Concat it is recognized as
    // our own disable and stays silent.
    [Fact]
    public void A_second_row_for_an_entry_we_just_disabled_is_not_blamed_on_the_user()
    {
        var cli = new FakeClaude()
            .Install("flaui-mcp@flaui-mcp", "local", @"C:\Proj", enabled: true)
            .Install("flaui-mcp@flaui-mcp", "local", @"c:\proj", enabled: true);   // same target, case-variant path
        var s = TempState();

        var warning = Remedy(cli, s).Apply();

        Assert.DoesNotContain("assuming you did", warning ?? "");
        Assert.Single(CollisionMarker.Read(s));   // recorded exactly once
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~A_second_row_for_an_entry_we_just_disabled_is_not_blamed_on_the_user"`
Expected: FAIL — the warning contains "assuming you did" (R5 misfires on the second row).

- [ ] **Step 3: Apply the `Concat` fix**

In `ClaudeCollisionRemedy.cs`, change the R5 check (@ :117) from:

```csharp
                if (!recorded.Any(m => CollisionMarker.SameEntry(m, entry)))
```

to:

```csharp
                if (!recorded.Concat(justDisabled).Any(m => CollisionMarker.SameEntry(m, entry)))
```

(`System.Linq` is already imported @ :2; `justDisabled` is in scope @ :62.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "FullyQualifiedName~A_second_row_for_an_entry_we_just_disabled_is_not_blamed_on_the_user"`
Expected: PASS.

- [ ] **Step 5: Run the full non-Desktop suite**

Run: `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop"`
Expected: `Failed: 0` (the existing `An_already_disabled_copy_with_no_marker_is_left_alone_and_reported` test — where nothing was disabled this run, so `justDisabled` is empty — still warns correctly).

- [ ] **Step 6: Commit**

```bash
git add src/FlaUI.Mcp.Server/Install/ClaudeCollisionRemedy.cs test/FlaUI.Mcp.Tests/Install/ClaudeCollisionRemedyTests.cs
git commit -m "fix(install): R5 must not blame the user for a disable earlier in the same Apply run"
```

(Tasks 5 and 6 are both one-liners over the same file; per spec §11 they MAY share a single commit. This plan keeps them separate for a clean per-goal history — combine only if you prefer.)

---

## Final verification (after all six tasks)

- [ ] **Full gate:** `dotnet test test/FlaUI.Mcp.Tests/FlaUI.Mcp.Tests.csproj --filter "Category!=Desktop"` → `Failed: 0`.
- [ ] **Warnings check:** `dotnet build src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj -c Debug` → `0 Warning(s)`.
- [ ] **Held invariant:** `git rev-parse --short master` == `2ffd6a1`; branch is `fix/distribution-live-defects`, 6 commits ahead of where Task 1 started; **not pushed**.
- [ ] Dispatch the final whole-implementation code reviewer (subagent-driven-development's terminal step). Do NOT push/tag/merge — v0.15.0 stays HELD.

---

## Goal → Task coverage (self-review)

| Spec goal | Where |
|---|---|
| 1. Corrupt `Record(B)` preserves A (`.bak`) + records B + promise holds | Task 2 (`Record_over_a_corrupt_marker_preserves...` for A+B+null return; `A_corrupt_marker_recovered_during_apply_still_promises_the_re_enable` for the Apply-level promise) |
| 2. Corrupt at uninstall → warn + recourse, not silent success | Task 3 (`A_corrupt_marker_at_uninstall_warns...`) |
| 3. Interrupted write never reads as valid | Task 2 (atomic `File.Move`) + Task 1 (torn file → `Corrupt`) |
| 4. `version > 1` left untouched | Task 1 (`FutureVersion` classification) + Task 2 (`Record` refuses) + Task 3 (`Restore` keeps) |
| 5. No `-1`/`-2` shown to the user | Task 5 (`DescribeExit`) |
| 6. R5 never fires for an entry we disabled this run | Task 6 (`Concat(justDisabled)`) |
| 7. `status` reports a corrupt/future marker | Task 4 (`DescribeCollisions` via `ReadState`) |

**`.bak` sweep on all four `Restore` branches** (spec §4 flow-control mandate + round-4 Test-Oracle): Task 3 (`The_bak_sweep_runs_on_every_restore_branch` theory).

## Deviation from the spec letter (flagged for review)

- Spec Testing § says "extend the pinned theory with `version:2`". This plan does that (Task 1 adds the `InlineData` to `A_corrupt_marker_reads_as_empty_and_never_throws`) **and** adds a separate, correctly-named `Read_is_empty_for_a_future_version_marker` fact — because a `version:2` marker is `FutureVersion`, not `Corrupt`, so pinning it only under a "corrupt" name would mislead a future reader. Both assert the same fail-safe (`Read` → empty). If the reviewer objects to touching the pinned theory at all, drop the `InlineData` and keep only the dedicated fact.
