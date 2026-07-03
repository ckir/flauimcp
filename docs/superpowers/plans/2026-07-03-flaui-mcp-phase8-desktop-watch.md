# Phase 8 — `desktop_watch` (UIA event streaming over stdio) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add push perception — `desktop_watch`/`desktop_unwatch`/`desktop_list_watches` tools that subscribe to UIA events and deliver them as MCP server→client notifications over the existing stdio pipe, so an agent reacts to desktop changes instead of polling snapshots.

**Architecture:** UIA callbacks arrive on COM RPC threads → a thin non-blocking **capture** reads only cheap cached props and PID-filters → a bounded **channel** → an **EventCoalescer** collapses/debounces → a single **worker** marshals payload-build onto the one query STA (INV-5 redaction, mint an event ref) → emits **off-STA** via `IMcpServer.SendNotificationAsync`. Subscription lifecycle reuses the Phase-6 `WindowManager.WindowInvalidated` chokepoint for auto-evict-on-close. The bulk of the logic is pure/headless-testable; the two irreducible feasibility risks (the exact FlaUI event+cache API, and whether the host surfaces server notifications) are resolved by **two load-bearing spikes first**.

**Tech Stack:** C#/.NET 10 (`net10.0-windows`), FlaUI.UIA3 5.0.0, `ModelContextProtocol` 1.4.0, `Microsoft.Extensions.Hosting`, xUnit.

**Source spec:** [`docs/superpowers/specs/2026-07-03-flaui-mcp-phase8-desktop-watch-design.md`](../specs/2026-07-03-flaui-mcp-phase8-desktop-watch-design.md) — GO after 5 AGY-AFTER panel rounds. Section numbers below (§N) refer to it.

**Branch:** `phase-8-desktop-watch` (already checked out). **Target version:** v0.7.7 → **v0.8.0**.

---

## Repo gate (use these EXACT commands — do NOT invent stricter flags)

- **Build:** `dotnet build -c Release`
- **Headless test (the CI gate):** `dotnet test -c Release --filter "Category!=Desktop"` — expected tail: `Passed!  - Failed: 0`.
- **Desktop test (maintainer/interactive session only — NOT in CI):** `dotnet test --filter "Category=Desktop&FullyQualifiedName~Watch"`.
- All new **pure-core** tests are headless (no `Category` trait). All new **UIA** tests carry `[Trait("Category", "Desktop")]`.

## Plan-discipline notes for implementers (READ FIRST)

- **Tasks 1–2 are SPIKES and GATE everything after them.** They are not throwaway: each produces a committed **findings note** under `docs/superpowers/spikes/`. Tasks 7–8 cite those notes for the exact FlaUI/SDK calls. If a spike’s finding contradicts an assumption written into a later task, STOP and report — do not "adapt" the wiring to compile against a guessed signature.
- **Do NOT fabricate FlaUI 5.0.0 event/CacheRequest signatures or the `IMcpServer` injection call.** Where a later task shows a FlaUI or SDK call site, it is marked `// PER SPIKE A` / `// PER SPIKE B`; fill it from the findings note, and if the real API differs in *shape* (extra arg, different return type, `Activate()`-only cache), report `[assumed] -> [actual] because <reason>` before writing it.
- **Oracle for the pure-core tasks:** the tests written in each task. Tests are provided complete — implement until they pass; do not rewrite them to match the code.

## File Structure

**New (Core, pure/headless — namespace `FlaUI.Mcp.Core.Watch`):**
- `src/FlaUI.Mcp.Core/Watch/WatchEventKind.cs` — the 4 v1 kinds + wire-token parse/format.
- `src/FlaUI.Mcp.Core/Watch/DesktopEventPayload.cs` — §4 notification wire record.
- `src/FlaUI.Mcp.Core/Watch/CapturedEventMeta.cs` — plain-data captured-event description + coalesce key.
- `src/FlaUI.Mcp.Core/Watch/EventCoalescer.cs` — §8 bounded buffer + debounce + coalescedCount/droppedCount.
- `src/FlaUI.Mcp.Core/Watch/FocusEventFilter.cs` — §7 PID delivery filter.
- `src/FlaUI.Mcp.Core/Watch/WatchPayloadBuilder.cs` + `IEventSourceReader` — §4/§10 payload build + INV-5.
- `src/FlaUI.Mcp.Core/Watch/WatchRegistry.cs` — subscription bookkeeping, caps, evict-on-close.

**New (Core, spike-gated UIA — same namespace):**
- `src/FlaUI.Mcp.Core/Watch/IUiaEventSource.cs` — seam: register/unregister UIA handlers for a subscription.
- `src/FlaUI.Mcp.Core/Watch/Uia3EventSource.cs` — FlaUI impl on the query STA (PER SPIKE A).
- `src/FlaUI.Mcp.Core/Watch/IEventSink.cs` — seam: emit one built payload (implemented in Server over `IMcpServer`).
- `src/FlaUI.Mcp.Core/Watch/WatchService.cs` — orchestrates registry + source + coalescer + channel.
- `src/FlaUI.Mcp.Core/Watch/WatchPump.cs` — the single worker loop (drain→STA build→emit); Start/StopAsync.

**Modified (Core):**
- `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs` — add `TooManyWatches`.
- `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs` — add the bounded, self-evicting **event-ref layer** (§16.5).

**New/Modified (Server — namespace `FlaUI.Mcp.Server.Tools`):**
- `src/FlaUI.Mcp.Server/Tools/WatchTools.cs` — the 3 `[McpServerTool]`s (+ `desktop_drain_events` only if Spike B → drain-mode).
- `src/FlaUI.Mcp.Server/Watch/McpEventSink.cs` — `IEventSink` over `IMcpServer.SendNotificationAsync` (PER SPIKE B).
- `src/FlaUI.Mcp.Server/Watch/WatchPumpHostedService.cs` — `IHostedService` wrapping `WatchPump` (PER SPIKE B).
- `src/FlaUI.Mcp.Server/Program.cs` — DI registrations + hosted-service registration.

**New tests:**
- `test/FlaUI.Mcp.Tests/Watch/WatchWireTests.cs`, `EventCoalescerTests.cs`, `WatchPayloadBuilderTests.cs`, `FocusEventFilterTests.cs`, `WatchRegistryTests.cs`, `EventRefLayerTests.cs` (all headless).
- `test/FlaUI.Mcp.Tests/Watch/DesktopWatchTests.cs` (`Category=Desktop`).

**Docs/version:** `.csproj`, `installer/flaui-mcp.iss`, `CHANGELOG.md`, `ROADMAP.md`, `README.md`, `.claude/skills/driving-flaui-mcp/SKILL.md`.

---

## Task 1 (SPIKE A): FlaUI 5.0.0 UIA event registration + CacheRequest API

**Purpose:** Resolve §14.1 + §16.1 against the *live* FlaUI 5.0.0 API. This is a load-bearing gate: Tasks 7–8 depend on the exact register/unregister signatures, the delegate shapes, whether a `CacheRequest` can be **passed** to a registration (vs. thread-local `Activate()`), and that the delivered source element exposes **cached** `ProcessId`/`RuntimeId` with no live cross-apartment round-trip.

**Files:**
- Create: `docs/superpowers/spikes/2026-07-03-spikeA-flaui-uia-events.md` (findings note — the deliverable)
- Create (throwaway, deleted at end of task): `test/FlaUI.Mcp.Tests/Watch/_SpikeAProbe.cs`

- [ ] **Step 1: Enumerate the real API surface.** In the FlaUI 5.0.0 assemblies at
  `~/.nuget/packages/flaui.core/5.0.0/lib/` and `~/.nuget/packages/flaui.uia3/5.0.0/lib/`, and via the
  installed source browser, find and record the exact signatures of: `AutomationElement.RegisterAutomationEvent`,
  `AutomationElement.RegisterStructureChangedEvent`, `Automation.RegisterFocusChangedEvent`, the handler
  registration return type (is it an `IDisposable`/`IAutomationEventHandler` you keep to unregister?), the
  unregister path (`RemoveAutomationEventHandler` / `Automation.UnregisterAllEvents` / `Dispose`), and the
  `CacheRequest` type (`.Add(...)`, `.Activate()` returning `IDisposable`, `AutomationElementMode`, `TreeScope`).
  Determine specifically: **does any Register\* overload accept a `CacheRequest` parameter, or is the only
  path `CacheRequest.Activate()` (thread-local)?** Record `EventId`s for `Window_Opened`/`Window_Closed`
  (`WindowPatternIdentifiers`/`AutomationElementIdentifiers`) and the `StructureChangedEvent` delegate’s args
  (`StructureChangeType`, `int[] runtimeId`).

- [ ] **Step 2: Write a Desktop probe** (`_SpikeAProbe.cs`, `[Trait("Category","Desktop")]`) that, on the
  query STA via a real `AutomationDispatcher`/`WindowManager`/`TestAppFixture` (copy the `OpenAsync` helper
  shape from `test/FlaUI.Mcp.Tests/Perception/FindTests.cs:14-22`), registers a `Window_Opened` handler
  **globally on the Desktop root** under a `CacheRequest` that caches ONLY `ProcessId`+`RuntimeId`, clicks
  `ModalButton` (AutomationId `ModalButton`) to open the "Modal" child window, and asserts: (a) a callback
  fires; (b) inside the callback the source’s **cached** `ProcessId` and `RuntimeId` are readable; (c)
  reading them does not require a live round-trip (record whether `element.Properties.ProcessId.ValueOrDefault`
  reads cache vs. re-queries — use the FlaUI cached accessor the API dictates).

- [ ] **Step 3: Prove the §16.1 poison guard.** If Step 1 shows the cache is `Activate()`-only, extend the
  probe: `Activate()` a `CacheRequest` scoped in a `using` around ONE registration on the query STA, then
  immediately run a synchronous `perception.SnapshotAsync(handle, new SnapshotOptions { FullProperties = true })`
  **on the same STA** and assert it returns a normal (un-poisoned, hydrated) tree. If a Register\* overload
  takes a `CacheRequest` param, record that this whole hazard is avoided and no `Activate()` is used.

- [ ] **Step 4: Run the probe.**

  Run: `dotnet test --filter "Category=Desktop&FullyQualifiedName~_SpikeAProbe"`
  Expected: PASS (callback fires; cached props readable; post-`Activate` snapshot un-poisoned).

- [ ] **Step 5: Write the findings note and delete the probe.** In
  `docs/superpowers/spikes/2026-07-03-spikeA-flaui-uia-events.md` record, verbatim, the confirmed signatures
  for: register (all 3 kinds) + the `TreeScope`/`EventId` args, unregister, `CacheRequest` construction and
  **whether it is a Register\* param or `Activate()`-only** (and if `Activate()`-only, the exact `using`
  scoping Task 7 must use), and the cached-property read accessor. Then `git rm` the throwaway probe.

- [ ] **Step 6: Commit.**

```bash
git add docs/superpowers/spikes/2026-07-03-spikeA-flaui-uia-events.md
git commit -m "spike(watch): confirm FlaUI 5.0.0 UIA event + CacheRequest API (Phase 8 gate)"
```

**BLOCKED handling:** If a required registration or cached-prop read is genuinely unavailable in FlaUI 5.0.0,
STOP and escalate — the whole capability depends on it; do not proceed to build against a guessed API.

---

## Task 2 (SPIKE B): `IMcpServer` notification over stdio + host-surfacing check

**Purpose:** Resolve §14.2 + §15a. Two questions: (1) can a background `IHostedService` in this SDK’s single
stdio session obtain an `IMcpServer` and successfully call `SendNotificationAsync(method, params)` to stdout?
(2) does the **host** (Claude Code) actually surface an unsolicited `notifications/flaui/desktop_event` to the
model? The answer decides **push-only vs push+drain vs drain-only** — the wire payload (§4) is identical either
way, but Task 8/9 branch on it.

**Files:**
- Create: `docs/superpowers/spikes/2026-07-03-spikeB-mcp-notification.md` (findings note + the delivery decision — the deliverable)
- Modify (temporary): a throwaway `[McpServerTool]` `debug_emit_test_event` in a new `src/FlaUI.Mcp.Server/Tools/_SpikeBTool.cs`

- [ ] **Step 1: Find the injection point.** In `ModelContextProtocol` 1.4.0
  (`~/.nuget/packages/modelcontextprotocol.core/1.4.0/lib/net10.0/ModelContextProtocol.Core.xml` + the
  `ModelContextProtocol.dll` at the same version), determine how a tool/hosted-service reaches `IMcpServer`
  (`ModelContextProtocol.Server.IMcpServer`): is it (a) injectable as a `[McpServerTool]` **method parameter**;
  (b) available via `RequestContext<CallToolRequestParams>.Server`; and/or (c) resolvable from the DI
  `IServiceProvider` after the host starts? Record the exact `SendNotificationAsync` overload —
  `McpSession.SendNotificationAsync<T>(string method, T notificationParams, ...)` is present (XML-doc verified);
  confirm the extension/instance path reachable from `IMcpServer` and the params-serialization convention (STJ;
  the record’s explicit `JsonPropertyName`s make the keys deterministic — Task 3 pins them).

- [ ] **Step 2: Emit one notification.** Add `_SpikeBTool.cs` with a `debug_emit_test_event` tool that obtains
  `IMcpServer` (by whichever mechanism Step 1 found) and calls
  `SendNotificationAsync("notifications/flaui/desktop_event", <a hardcoded DesktopEventPayload-shaped object>)`.
  Build (`dotnet build -c Release`).

- [ ] **Step 3: Host-surfacing check (the load-bearing half).** Install/point Claude Code at this build,
  reconnect (`/mcp`), invoke `debug_emit_test_event`, and observe whether the `notifications/flaui/desktop_event`
  notification is surfaced into the model’s conversation context. Record the result. If time permits, repeat
  against one other MCP client (e.g. MCP Inspector) to see whether push is host-agnostic. **This step needs the
  user** (install + `/mcp` reconnect are human-gated) — surface it as a checkpoint, do not attempt to self-drive
  the install.

- [ ] **Step 4: Record the decision.** In `docs/superpowers/spikes/2026-07-03-spikeB-mcp-notification.md`
  record: the confirmed `IMcpServer` acquisition mechanism, the exact `SendNotificationAsync` call, the
  serialization path, and the **DELIVERY DECISION**:
  - **push-only** — host surfaces notifications reliably → build §6 pipeline as written; no drain tool.
  - **push+drain** — push works but is not reliably surfaced everywhere → also build the §15a drainable buffer +
    `desktop_drain_events` tool (Task 8 note + Task 9).
  - **drain-only** — host does not surface push → skip the emit, buffer server-side, ship `desktop_drain_events`.
  Remove/neutralize `_SpikeBTool.cs` (delete, or keep gated behind a debug flag — your call, but it must NOT
  ship as a public tool).

- [ ] **Step 5: Commit.**

```bash
git add docs/superpowers/spikes/2026-07-03-spikeB-mcp-notification.md
git rm src/FlaUI.Mcp.Server/Tools/_SpikeBTool.cs   # (or keep neutralized; if kept, git add it)
git commit -m "spike(watch): confirm IMcpServer stdio notification + host-surfacing; record delivery decision"
```

**Gate:** Tasks 3–7 (pure core) are delivery-mode-independent and may proceed regardless. Tasks 8–10 branch on
the decision. If **drain-only**, Task 9’s emit path is replaced by the buffer/drain path (noted inline there).

---

## Task 3: Wire types — `WatchEventKind`, `DesktopEventPayload`, `TooManyWatches`

**Files:**
- Create: `src/FlaUI.Mcp.Core/Watch/WatchEventKind.cs`
- Create: `src/FlaUI.Mcp.Core/Watch/DesktopEventPayload.cs`
- Modify: `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs` (add one enum member)
- Test: `test/FlaUI.Mcp.Tests/Watch/WatchWireTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
// test/FlaUI.Mcp.Tests/Watch/WatchWireTests.cs
using System.Text.Json;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WatchWireTests
{
    [Theory]
    [InlineData("window_opened", WatchEventKind.WindowOpened)]
    [InlineData("window_closed", WatchEventKind.WindowClosed)]
    [InlineData("focus_changed", WatchEventKind.FocusChanged)]
    [InlineData("structure_changed", WatchEventKind.StructureChanged)]
    public void TryParse_roundtrips_wire_tokens(string token, WatchEventKind expected)
    {
        Assert.True(WatchEventKinds.TryParse(token, out var k));
        Assert.Equal(expected, k);
        Assert.Equal(token, WatchEventKinds.ToWire(k));
    }

    [Fact]
    public void TryParse_rejects_unknown_token()
    {
        Assert.False(WatchEventKinds.TryParse("property_changed", out _));
        Assert.False(WatchEventKinds.TryParse("", out _));
    }

    [Fact]
    public void Payload_serializes_required_keys_and_omits_null_optionals()
    {
        var p = new DesktopEventPayload(
            SubscriptionId: "s1", Event: "window_closed", Window: "w1",
            Ref: null, ControlType: null, Name: null, Bounds: null,
            CoalescedCount: 1, TimestampUtc: "2026-07-03T10:00:00.000Z");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(p));
        var root = doc.RootElement;
        // required present
        Assert.Equal("s1", root.GetProperty("subscriptionId").GetString());
        Assert.Equal("window_closed", root.GetProperty("event").GetString());
        Assert.Equal("w1", root.GetProperty("window").GetString());
        Assert.Equal(1, root.GetProperty("coalescedCount").GetInt32());
        Assert.Equal("2026-07-03T10:00:00.000Z", root.GetProperty("timestampUtc").GetString());
        // nullable optionals omitted
        Assert.False(root.TryGetProperty("ref", out _));
        Assert.False(root.TryGetProperty("controlType", out _));
        Assert.False(root.TryGetProperty("name", out _));
        Assert.False(root.TryGetProperty("bounds", out _));
    }

    [Fact]
    public void Payload_emits_ref_and_bounds_when_present_under_wire_keys()
    {
        var p = new DesktopEventPayload("s1", "focus_changed", "w1",
            Ref: "e42", ControlType: "Edit", Name: "Search", Bounds: new[] { 1, 2, 3, 4 },
            CoalescedCount: 3, TimestampUtc: "2026-07-03T10:00:00.000Z");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(p));
        var root = doc.RootElement;
        Assert.Equal("e42", root.GetProperty("ref").GetString());
        Assert.Equal("Edit", root.GetProperty("controlType").GetString());
        Assert.Equal("Search", root.GetProperty("name").GetString());
        Assert.Equal(4, root.GetProperty("bounds").GetArrayLength());
        Assert.Equal(3, root.GetProperty("coalescedCount").GetInt32());
    }
}
```

- [ ] **Step 2: Run to verify failure.**

  Run: `dotnet test --filter "FullyQualifiedName~WatchWireTests"`
  Expected: FAIL to compile (`WatchEventKind`, `DesktopEventPayload` not defined).

- [ ] **Step 3: Implement the types.**

```csharp
// src/FlaUI.Mcp.Core/Watch/WatchEventKind.cs
namespace FlaUI.Mcp.Core.Watch;

/// <summary>The v1 event kinds (§5). Wire tokens are stable; the enum is internal ordering only.</summary>
public enum WatchEventKind { WindowOpened, WindowClosed, FocusChanged, StructureChanged }

public static class WatchEventKinds
{
    public static string ToWire(WatchEventKind k) => k switch
    {
        WatchEventKind.WindowOpened => "window_opened",
        WatchEventKind.WindowClosed => "window_closed",
        WatchEventKind.FocusChanged => "focus_changed",
        WatchEventKind.StructureChanged => "structure_changed",
        _ => throw new System.ArgumentOutOfRangeException(nameof(k), k, "unknown WatchEventKind"),
    };

    public static bool TryParse(string token, out WatchEventKind kind)
    {
        switch (token)
        {
            case "window_opened": kind = WatchEventKind.WindowOpened; return true;
            case "window_closed": kind = WatchEventKind.WindowClosed; return true;
            case "focus_changed": kind = WatchEventKind.FocusChanged; return true;
            case "structure_changed": kind = WatchEventKind.StructureChanged; return true;
            default: kind = default; return false;
        }
    }
}
```

```csharp
// src/FlaUI.Mcp.Core/Watch/DesktopEventPayload.cs
using System.Text.Json.Serialization;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>§4 notification wire contract for ONE emitted event. Required keys always present;
/// nullable keys (ref/controlType/name/bounds) are JsonIgnore-when-null. "event"/"ref" are JSON
/// keys that collide with C# keywords, so they carry explicit JsonPropertyName. `bounds` is
/// [x,y,w,h]. `timestampUtc` is a pre-formatted ISO-8601 Z string (built by WatchPayloadBuilder).</summary>
public sealed record DesktopEventPayload(
    [property: JsonPropertyName("subscriptionId")] string SubscriptionId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ref,
    [property: JsonPropertyName("controlType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ControlType,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonPropertyName("bounds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? Bounds,
    [property: JsonPropertyName("coalescedCount")] int CoalescedCount,
    [property: JsonPropertyName("timestampUtc")] string TimestampUtc);
```

  In `src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs`, add `TooManyWatches` as the last enum member (after
  `ClipboardHoldsNonText`):

```csharp
    SinkInterlocked,
    ClipboardHoldsNonText,
    TooManyWatches
```

- [ ] **Step 4: Run to verify pass.**

  Run: `dotnet test --filter "FullyQualifiedName~WatchWireTests"`
  Expected: PASS (7 test cases green).

- [ ] **Step 5: Commit.**

```bash
git add src/FlaUI.Mcp.Core/Watch/WatchEventKind.cs src/FlaUI.Mcp.Core/Watch/DesktopEventPayload.cs \
        src/FlaUI.Mcp.Core/Errors/ToolErrorCode.cs test/FlaUI.Mcp.Tests/Watch/WatchWireTests.cs
git commit -m "feat(watch): wire types WatchEventKind + DesktopEventPayload + TooManyWatches code"
```

---

## Task 4: `CapturedEventMeta` + `EventCoalescer` (§8 back-pressure/coalescing)

**Files:**
- Create: `src/FlaUI.Mcp.Core/Watch/CapturedEventMeta.cs`
- Create: `src/FlaUI.Mcp.Core/Watch/EventCoalescer.cs`
- Test: `test/FlaUI.Mcp.Tests/Watch/EventCoalescerTests.cs`

**Design contract (from §8):** The coalescer is a pure, time-injected buffer of *pending aggregates* keyed by
a **coalesce key**. `Offer` adds/merges an event under its key (incrementing `CoalescedCount`, updating
`LastSeenUtc`). `Drain(now)` removes and returns aggregates that are *ready*: non-`structure_changed` keys are
ready immediately; `structure_changed` keys are ready only once quiet for `debounceMs` (settle). Capacity =
max distinct pending keys; on overflow, evict the **oldest** distinct key and report its `SubscriptionId` so the
caller bumps that sub’s `droppedCount` (loss is observable, never silent).

- [ ] **Step 1: Write the failing tests.**

```csharp
// test/FlaUI.Mcp.Tests/Watch/EventCoalescerTests.cs
using System;
using System.Linq;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class EventCoalescerTests
{
    private static CapturedEventMeta Meta(string sub, WatchEventKind kind, string rid, DateTime t) =>
        new(sub, kind, SourceProcessId: 100, SourceRuntimeId: rid, TimestampUtc: t);

    [Fact]
    public void Same_structure_key_collapses_and_counts()
    {
        var c = new EventCoalescer(capacity: 256, debounceMs: 100);
        var t0 = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);
        string key = "s1|structure_changed|w1";  // subscribedScope-based key
        Assert.Null(c.Offer(key, Meta("s1", WatchEventKind.StructureChanged, "r1", t0), t0));
        Assert.Null(c.Offer(key, Meta("s1", WatchEventKind.StructureChanged, "r2", t0.AddMilliseconds(30)), t0.AddMilliseconds(30)));
        // still within debounce quiet window -> not ready
        Assert.Empty(c.Drain(t0.AddMilliseconds(50)));
        // quiet for >= debounce -> one settled aggregate with coalescedCount 2
        var ready = c.Drain(t0.AddMilliseconds(200));
        var agg = Assert.Single(ready);
        Assert.Equal(2, agg.CoalescedCount);
        Assert.Equal(WatchEventKind.StructureChanged, agg.Meta.Kind);
    }

    [Fact]
    public void Nonstructure_events_are_ready_immediately()
    {
        var c = new EventCoalescer(256, 100);
        var t0 = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);
        c.Offer("s1|focus_changed|r1", Meta("s1", WatchEventKind.FocusChanged, "r1", t0), t0);
        var ready = c.Drain(t0);   // no debounce for focus
        var agg = Assert.Single(ready);
        Assert.Equal(1, agg.CoalescedCount);
    }

    [Fact]
    public void Distinct_keys_stay_separate()
    {
        var c = new EventCoalescer(256, 100);
        var t0 = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);
        c.Offer("s1|focus_changed|r1", Meta("s1", WatchEventKind.FocusChanged, "r1", t0), t0);
        c.Offer("s1|focus_changed|r2", Meta("s1", WatchEventKind.FocusChanged, "r2", t0), t0);
        Assert.Equal(2, c.Drain(t0).Count);
    }

    [Fact]
    public void Overflow_evicts_oldest_distinct_key_and_reports_its_subscription()
    {
        var c = new EventCoalescer(capacity: 2, debounceMs: 100);
        var t0 = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(c.Offer("s1|focus_changed|r1", Meta("s1", WatchEventKind.FocusChanged, "r1", t0), t0));
        Assert.Null(c.Offer("s2|focus_changed|r2", Meta("s2", WatchEventKind.FocusChanged, "r2", t0.AddMilliseconds(1)), t0.AddMilliseconds(1)));
        // third distinct key overflows capacity 2 -> evict oldest (s1) and report it
        var dropped = c.Offer("s3|focus_changed|r3", Meta("s3", WatchEventKind.FocusChanged, "r3", t0.AddMilliseconds(2)), t0.AddMilliseconds(2));
        Assert.Equal("s1", dropped);
        var keysLeft = c.Drain(t0.AddMilliseconds(3)).Select(a => a.Meta.SubscriptionId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "s2", "s3" }, keysLeft);
    }

    [Fact]
    public void Merging_into_existing_key_does_not_count_as_overflow()
    {
        var c = new EventCoalescer(capacity: 1, debounceMs: 100);
        var t0 = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(c.Offer("s1|focus_changed|r1", Meta("s1", WatchEventKind.FocusChanged, "r1", t0), t0));
        Assert.Null(c.Offer("s1|focus_changed|r1", Meta("s1", WatchEventKind.FocusChanged, "r1", t0.AddMilliseconds(5)), t0.AddMilliseconds(5)));
        var agg = Assert.Single(c.Drain(t0.AddMilliseconds(6)));
        Assert.Equal(2, agg.CoalescedCount);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

  Run: `dotnet test --filter "FullyQualifiedName~EventCoalescerTests"`
  Expected: FAIL to compile (`CapturedEventMeta`, `EventCoalescer` not defined).

- [ ] **Step 3: Implement the types.**

```csharp
// src/FlaUI.Mcp.Core/Watch/CapturedEventMeta.cs
namespace FlaUI.Mcp.Core.Watch;

/// <summary>Plain-data description of one captured UIA event, produced by the COM capture thread from
/// CACHED props only (no live UIA read) — so the coalescer/payload logic stays FlaUI-free and headless.
/// The live source element (if any) travels separately in the wiring envelope, never through here.
/// SourceRuntimeId is the cached RuntimeId joined as "a,b,c" ("" if unreadable, §16.4).</summary>
public readonly record struct CapturedEventMeta(
    string SubscriptionId,
    WatchEventKind Kind,
    int SourceProcessId,
    string SourceRuntimeId,
    System.DateTime TimestampUtc)
{
    /// <summary>Kind-dependent coalesce key (§8). structure_changed collapses to the subscription's
    /// subscribedScope (passed in — known at subscribe time, no source read), NOT the per-child source
    /// RuntimeId (which is unique per changed child and would defeat collapse). All other kinds key on
    /// the cached source RuntimeId (they are not storms).</summary>
    public string CoalesceKey(string subscribedScope) => Kind == WatchEventKind.StructureChanged
        ? $"{SubscriptionId}|structure_changed|{subscribedScope}"
        : $"{SubscriptionId}|{WatchEventKinds.ToWire(Kind)}|{SourceRuntimeId}";
}
```

```csharp
// src/FlaUI.Mcp.Core/Watch/EventCoalescer.cs
using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>§8 pure back-pressure/coalescing core. NOT thread-safe — the wiring drives it from the
/// single worker thread. Time is injected for deterministic tests. Holds one pending aggregate per
/// coalesce key; structure_changed aggregates debounce (settle) before becoming ready.</summary>
public sealed class EventCoalescer
{
    public sealed record Pending(CapturedEventMeta Meta, int CoalescedCount, DateTime FirstSeenUtc, DateTime LastSeenUtc);

    private sealed class Slot { public CapturedEventMeta Meta; public int Count; public DateTime First; public DateTime Last; }

    private readonly int _capacity;
    private readonly int _debounceMs;
    private readonly Dictionary<string, Slot> _pending = new();
    private readonly LinkedList<string> _order = new(); // insertion order for oldest-eviction

    public EventCoalescer(int capacity = 256, int debounceMs = 100)
    {
        _capacity = capacity < 1 ? 1 : capacity;
        _debounceMs = debounceMs < 0 ? 0 : debounceMs;
    }

    /// <summary>True when there is at least one pending aggregate. The WatchPump uses this to choose
    /// block-on-channel (idle) vs. race-a-short-timer (items settling) — Task 8's conditional wake.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>Offer an event under its coalesce key. Merges into an existing key (bumps count/lastSeen)
    /// or inserts a new one. If inserting a NEW key exceeds capacity, evict the OLDEST distinct key and
    /// return its SubscriptionId so the caller bumps that sub's droppedCount; otherwise returns null.</summary>
    public string? Offer(string coalesceKey, CapturedEventMeta meta, DateTime nowUtc)
    {
        if (_pending.TryGetValue(coalesceKey, out var slot))
        {
            slot.Meta = meta;          // keep the freshest meta (latest timestamp/source)
            slot.Count++;
            slot.Last = nowUtc;
            return null;
        }

        string? droppedSub = null;
        if (_pending.Count >= _capacity)
        {
            var oldestKey = _order.First!.Value;
            _order.RemoveFirst();
            if (_pending.Remove(oldestKey, out var evicted))
                droppedSub = evicted.Meta.SubscriptionId;
        }

        _pending[coalesceKey] = new Slot { Meta = meta, Count = 1, First = nowUtc, Last = nowUtc };
        _order.AddLast(coalesceKey);
        return droppedSub;
    }

    /// <summary>Remove and return every aggregate ready to emit at nowUtc. structure_changed is ready once
    /// quiet for debounceMs; every other kind is ready immediately.</summary>
    public IReadOnlyList<Pending> Drain(DateTime nowUtc)
    {
        var ready = new List<Pending>();
        var emit = new List<string>();
        foreach (var (key, slot) in _pending)
        {
            bool settled = slot.Meta.Kind != WatchEventKind.StructureChanged
                || (nowUtc - slot.Last).TotalMilliseconds >= _debounceMs;
            if (settled) emit.Add(key);
        }
        foreach (var key in emit)
        {
            var s = _pending[key];
            ready.Add(new Pending(s.Meta, s.Count, s.First, s.Last));
            _pending.Remove(key);
            _order.Remove(key);
        }
        return ready;
    }
}
```

- [ ] **Step 4: Run to verify pass.**

  Run: `dotnet test --filter "FullyQualifiedName~EventCoalescerTests"`
  Expected: PASS (5 tests green).

- [ ] **Step 5: Commit.**

```bash
git add src/FlaUI.Mcp.Core/Watch/CapturedEventMeta.cs src/FlaUI.Mcp.Core/Watch/EventCoalescer.cs \
        test/FlaUI.Mcp.Tests/Watch/EventCoalescerTests.cs
git commit -m "feat(watch): EventCoalescer bounded buffer + debounce + coalescedCount/droppedCount"
```

---

## Task 5: `FocusEventFilter` (§7) + `WatchPayloadBuilder`/`IEventSourceReader` (§4/§10 INV-5)

**Files:**
- Create: `src/FlaUI.Mcp.Core/Watch/FocusEventFilter.cs`
- Create: `src/FlaUI.Mcp.Core/Watch/WatchPayloadBuilder.cs` (contains `IEventSourceReader`)
- Test: `test/FlaUI.Mcp.Tests/Watch/FocusEventFilterTests.cs`
- Test: `test/FlaUI.Mcp.Tests/Watch/WatchPayloadBuilderTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
// test/FlaUI.Mcp.Tests/Watch/FocusEventFilterTests.cs
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class FocusEventFilterTests
{
    [Theory]
    [InlineData(WatchEventKind.FocusChanged, 100, 100, true)]   // same process -> deliver
    [InlineData(WatchEventKind.FocusChanged, 100, 200, false)]  // foreign process -> drop (§7)
    [InlineData(WatchEventKind.WindowOpened, 100, 100, true)]
    [InlineData(WatchEventKind.WindowOpened, 100, 200, false)]
    [InlineData(WatchEventKind.FocusChanged, 100, 0, false)]    // unreadable PID (§16.4) -> drop
    [InlineData(WatchEventKind.FocusChanged, 0, 100, false)]    // unreadable sub PID -> drop
    [InlineData(WatchEventKind.StructureChanged, 100, 200, true)] // scope-registered -> always passes
    public void ShouldDeliver_enforces_pid_filter(WatchEventKind kind, int subPid, int srcPid, bool expected)
        => Assert.Equal(expected, FocusEventFilter.ShouldDeliver(kind, subPid, srcPid));
}
```

```csharp
// test/FlaUI.Mcp.Tests/Watch/WatchPayloadBuilderTests.cs
using System;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WatchPayloadBuilderTests
{
    private sealed class FakeReader : IEventSourceReader
    {
        public bool HasSource { get; init; } = true;
        public bool IsPassword { get; init; }
        public string? ControlTypeValue { get; init; }
        public string? NameValue { get; init; }
        public int[]? BoundsValue { get; init; }
        public string? RefValue { get; init; }
        public string? ControlType => ControlTypeValue;
        public string? Name => NameValue;
        public int[]? Bounds => BoundsValue;
        public string? MintRef() => RefValue;
    }

    private static CapturedEventMeta Meta(WatchEventKind kind) => new(
        "s1", kind, 100, "r1", new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Builds_full_payload_from_present_source()
    {
        var reader = new FakeReader { ControlTypeValue = "Edit", NameValue = "Search",
            BoundsValue = new[] { 1, 2, 3, 4 }, RefValue = "e42" };
        var p = WatchPayloadBuilder.Build(Meta(WatchEventKind.FocusChanged), "w1", coalescedCount: 2, reader);
        Assert.Equal("s1", p.SubscriptionId);
        Assert.Equal("focus_changed", p.Event);
        Assert.Equal("w1", p.Window);
        Assert.Equal("e42", p.Ref);
        Assert.Equal("Edit", p.ControlType);
        Assert.Equal("Search", p.Name);
        Assert.Equal(new[] { 1, 2, 3, 4 }, p.Bounds);
        Assert.Equal(2, p.CoalescedCount);
        Assert.Equal("2026-07-03T10:00:00.000Z", p.TimestampUtc);
    }

    [Fact]
    public void Password_source_redacts_name_INV5()
    {
        var reader = new FakeReader { IsPassword = true, ControlTypeValue = "Edit",
            NameValue = "hunter2-NEVER-LEAK", RefValue = "e1" };
        var p = WatchPayloadBuilder.Build(Meta(WatchEventKind.FocusChanged), "w1", 1, reader);
        Assert.Equal("[REDACTED]", p.Name);
        Assert.DoesNotContain("hunter2", p.Name);
    }

    [Fact]
    public void Null_source_yields_null_ref_name_bounds_controltype()
    {
        var reader = new FakeReader { HasSource = false };
        var p = WatchPayloadBuilder.Build(Meta(WatchEventKind.WindowClosed), "w1", 1, reader);
        Assert.Null(p.Ref);
        Assert.Null(p.Name);
        Assert.Null(p.Bounds);
        Assert.Null(p.ControlType);
        Assert.Equal("window_closed", p.Event);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

  Run: `dotnet test --filter "FullyQualifiedName~FocusEventFilterTests|FullyQualifiedName~WatchPayloadBuilderTests"`
  Expected: FAIL to compile (`FocusEventFilter`, `WatchPayloadBuilder`, `IEventSourceReader` not defined).

- [ ] **Step 3: Implement the types.**

```csharp
// src/FlaUI.Mcp.Core/Watch/FocusEventFilter.cs
namespace FlaUI.Mcp.Core.Watch;

/// <summary>§7 delivery filter. focus_changed and the global window_opened/window_closed registrations are
/// desktop-wide; deliver ONLY when the source PID matches the subscription window's PID. A missing/zero PID
/// (elevated/inaccessible source, §16.4) is undeliverable -> drop. structure_changed is scope-registered
/// (not global) so it always passes this gate.</summary>
public static class FocusEventFilter
{
    public static bool ShouldDeliver(WatchEventKind kind, int subscriptionProcessId, int sourceProcessId)
    {
        if (kind == WatchEventKind.StructureChanged) return true;
        if (subscriptionProcessId <= 0 || sourceProcessId <= 0) return false;
        return subscriptionProcessId == sourceProcessId;
    }
}
```

```csharp
// src/FlaUI.Mcp.Core/Watch/WatchPayloadBuilder.cs
using System.Globalization;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Abstracts the STA-side reads needed to build a payload from an already-resolved source element,
/// so the builder is headless-testable with a fake. The live implementation (Task 8) reads on the query STA
/// and is fail-soft per read. IsPassword MUST be fail-closed (RedactionPolicy.IsPasswordOrFailClosed).</summary>
public interface IEventSourceReader
{
    bool HasSource { get; }        // false for window_closed (no live source to read)
    bool IsPassword { get; }       // INV-5 (fail-closed in the live impl)
    string? ControlType { get; }
    string? Name { get; }          // RAW; the builder redacts when IsPassword
    int[]? Bounds { get; }         // [x,y,w,h] or null (element may be gone by delivery)
    string? MintRef();             // mint an event ref (bounded layer, §16.5) or null
}

/// <summary>§4 payload assembly + §10 INV-5 redaction. Pure given a reader. The CALLER (WatchPump, Task 8)
/// decides which element the reader wraps per event kind: the event SOURCE for focus/window_opened, but the
/// subscribed SCOPE for structure_changed (§8/R5 — a coalesced structure event refs the scope/container, not
/// the transient child). So MintRef() here just mints whatever the reader points at — the scope-vs-source
/// choice is upstream, not in this builder.</summary>
public static class WatchPayloadBuilder
{
    public static DesktopEventPayload Build(CapturedEventMeta meta, string windowId, int coalescedCount, IEventSourceReader reader)
    {
        string? @ref = null, controlType = null, name = null;
        int[]? bounds = null;
        if (reader.HasSource)
        {
            @ref = reader.MintRef();
            controlType = reader.ControlType;
            bounds = reader.Bounds;
            name = reader.IsPassword ? "[REDACTED]" : reader.Name; // INV-5
        }
        return new DesktopEventPayload(
            meta.SubscriptionId, WatchEventKinds.ToWire(meta.Kind), windowId,
            @ref, controlType, name, bounds, coalescedCount,
            meta.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
    }
}
```

- [ ] **Step 4: Run to verify pass.**

  Run: `dotnet test --filter "FullyQualifiedName~FocusEventFilterTests|FullyQualifiedName~WatchPayloadBuilderTests"`
  Expected: PASS (10 test cases green).

- [ ] **Step 5: Commit.**

```bash
git add src/FlaUI.Mcp.Core/Watch/FocusEventFilter.cs src/FlaUI.Mcp.Core/Watch/WatchPayloadBuilder.cs \
        test/FlaUI.Mcp.Tests/Watch/FocusEventFilterTests.cs test/FlaUI.Mcp.Tests/Watch/WatchPayloadBuilderTests.cs
git commit -m "feat(watch): FocusEventFilter (PID gate) + WatchPayloadBuilder (INV-5 redaction, null-source)"
```

---

## Task 6: `WatchRegistry` — caps, idempotency, evict-on-close, list

**Files:**
- Create: `src/FlaUI.Mcp.Core/Watch/WatchRegistry.cs`
- Test: `test/FlaUI.Mcp.Tests/Watch/WatchRegistryTests.cs`

**Design contract (§3/§9):** pure bookkeeping. `Create` mints a never-reused `s{n}`, enforcing per-window
(`MaxPerWindow`) and per-session (`MaxPerSession`) caps atomically → `TooManyWatches` (§3, round-4 Seat Q).
`Remove` is idempotent at the tool layer (returns whether it existed). `RemoveByWindow` powers Phase-6
auto-evict. `IncrementDropped` records per-sub coalescing loss. `List`/`TryGet` power `desktop_list_watches`
and lifecycle. UIA registration itself is NOT here — the `WatchService` (Task 8) calls the `IUiaEventSource`
seam around `Create`/`Remove` transactionally.

- [ ] **Step 1: Write the failing tests.**

```csharp
// test/FlaUI.Mcp.Tests/Watch/WatchRegistryTests.cs
using System.Linq;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WatchRegistryTests
{
    private static readonly WatchEventKind[] Focus = { WatchEventKind.FocusChanged };

    [Fact]
    public void Create_mints_monotonic_never_reused_ids()
    {
        var r = new WatchRegistry();
        var a = r.Create("w1", Focus, null);
        var b = r.Create("w1", Focus, null);
        Assert.Equal("s1", a);
        Assert.Equal("s2", b);
        r.Remove(a);
        var c = r.Create("w1", Focus, null);
        Assert.Equal("s3", c); // counter never resets (no id reuse)
    }

    [Fact]
    public void Create_enforces_per_window_cap_TooManyWatches()
    {
        var r = new WatchRegistry();
        for (int i = 0; i < WatchRegistry.MaxPerWindow; i++) r.Create("w1", Focus, null);
        var ex = Assert.Throws<ToolException>(() => r.Create("w1", Focus, null));
        Assert.Equal(ToolErrorCode.TooManyWatches, ex.Code);
        // a DIFFERENT window is unaffected (until the session cap)
        Assert.Equal("s" + (WatchRegistry.MaxPerWindow + 1), r.Create("w2", Focus, null));
    }

    [Fact]
    public void Create_enforces_per_session_cap_TooManyWatches()
    {
        var r = new WatchRegistry();
        int made = 0;
        // spread across many windows so per-window cap never trips first
        for (int w = 1; made < WatchRegistry.MaxPerSession; w++)
            for (int i = 0; i < WatchRegistry.MaxPerWindow && made < WatchRegistry.MaxPerSession; i++)
            { r.Create("w" + w, Focus, null); made++; }
        var ex = Assert.Throws<ToolException>(() => r.Create("wZ", Focus, null));
        Assert.Equal(ToolErrorCode.TooManyWatches, ex.Code);
    }

    [Fact]
    public void Remove_is_idempotent()
    {
        var r = new WatchRegistry();
        var a = r.Create("w1", Focus, null);
        Assert.True(r.Remove(a));
        Assert.False(r.Remove(a));     // already gone -> false (tool layer maps to ok:true)
        Assert.False(r.Remove("s999")); // unknown -> false
    }

    [Fact]
    public void RemoveByWindow_evicts_all_that_window_returns_ids()
    {
        var r = new WatchRegistry();
        var a = r.Create("w1", Focus, null);
        var b = r.Create("w1", Focus, null);
        var c = r.Create("w2", Focus, null);
        var evicted = r.RemoveByWindow("w1").OrderBy(x => x).ToArray();
        Assert.Equal(new[] { a, b }, evicted);
        Assert.True(r.TryGet(c, out _)); // w2 sub survives
        Assert.False(r.TryGet(a, out _));
    }

    [Fact]
    public void List_and_TryGet_report_kinds_scope_dropped()
    {
        var r = new WatchRegistry();
        var a = r.Create("w1", new[] { WatchEventKind.StructureChanged }, "e5");
        r.IncrementDropped(a);
        r.IncrementDropped(a);
        Assert.True(r.TryGet(a, out var info));
        Assert.Equal("w1", info!.WindowId);
        Assert.Equal("e5", info.Scope);
        Assert.Equal(2, info.DroppedCount);
        Assert.Contains(WatchEventKind.StructureChanged, info.Kinds);
        var listed = Assert.Single(r.List());
        Assert.Equal(a, listed.SubscriptionId);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

  Run: `dotnet test --filter "FullyQualifiedName~WatchRegistryTests"`
  Expected: FAIL to compile (`WatchRegistry`, `WatchInfo` not defined).

- [ ] **Step 3: Implement `WatchRegistry`.**

```csharp
// src/FlaUI.Mcp.Core/Watch/WatchRegistry.cs
using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Public view of one subscription (for desktop_list_watches, §3).</summary>
public sealed record WatchInfo(
    string SubscriptionId, string WindowId,
    IReadOnlyList<WatchEventKind> Kinds, string? Scope, int DroppedCount);

/// <summary>Pure subscription bookkeeping (§3/§9). Thread-safe (locked): the tool layer and the Phase-6
/// WindowInvalidated evict path both touch it. UIA (un)registration is NOT here — WatchService drives the
/// IUiaEventSource seam transactionally around Create/Remove.</summary>
public sealed class WatchRegistry
{
    public const int MaxPerWindow = 5;   // §3 (round-4 Seat Q): bound UIA COM resource use per window
    public const int MaxPerSession = 20; // and per session

    private sealed class Sub
    {
        public required string Id;
        public required string WindowId;
        public required IReadOnlyList<WatchEventKind> Kinds;
        public string? Scope;
        public int DroppedCount;
    }

    private readonly object _gate = new();
    private int _counter;
    private readonly Dictionary<string, Sub> _subs = new();

    /// <summary>Reserve a new subscription id, enforcing per-window AND per-session caps atomically.
    /// Throws TooManyWatches when either cap is reached. The caller (WatchService) then registers UIA
    /// handlers and, on failure, calls Remove(id) so a failed registration leaves no phantom.</summary>
    public string Create(string windowId, IReadOnlyList<WatchEventKind> kinds, string? scope)
    {
        lock (_gate)
        {
            int perWindow = _subs.Values.Count(s => s.WindowId == windowId);
            if (perWindow >= MaxPerWindow)
                throw new ToolException(ToolErrorCode.TooManyWatches,
                    $"Window '{windowId}' already has {MaxPerWindow} active watches (the per-window cap).",
                    "desktop_unwatch one of its subscriptions, or reuse an existing watch");
            if (_subs.Count >= MaxPerSession)
                throw new ToolException(ToolErrorCode.TooManyWatches,
                    $"This session already has {MaxPerSession} active watches (the per-session cap).",
                    "desktop_unwatch a subscription you no longer need");
            var id = $"s{++_counter}";
            _subs[id] = new Sub { Id = id, WindowId = windowId, Kinds = kinds, Scope = scope };
            return id;
        }
    }

    /// <summary>Idempotent: true if the sub existed and was removed, false otherwise (unknown/already-gone).</summary>
    public bool Remove(string subscriptionId)
    {
        lock (_gate) return _subs.Remove(subscriptionId);
    }

    /// <summary>Evict every subscription on a (closed) window; returns the removed ids (for handler cleanup).</summary>
    public IReadOnlyList<string> RemoveByWindow(string windowId)
    {
        lock (_gate)
        {
            var ids = _subs.Values.Where(s => s.WindowId == windowId).Select(s => s.Id).ToList();
            foreach (var id in ids) _subs.Remove(id);
            return ids;
        }
    }

    public void IncrementDropped(string subscriptionId)
    {
        lock (_gate) { if (_subs.TryGetValue(subscriptionId, out var s)) s.DroppedCount++; }
    }

    public bool TryGet(string subscriptionId, out WatchInfo? info)
    {
        lock (_gate)
        {
            if (_subs.TryGetValue(subscriptionId, out var s))
            { info = new WatchInfo(s.Id, s.WindowId, s.Kinds, s.Scope, s.DroppedCount); return true; }
            info = null; return false;
        }
    }

    public IReadOnlyList<WatchInfo> List()
    {
        lock (_gate)
            return _subs.Values.Select(s => new WatchInfo(s.Id, s.WindowId, s.Kinds, s.Scope, s.DroppedCount)).ToList();
    }
}
```

- [ ] **Step 4: Run to verify pass.**

  Run: `dotnet test --filter "FullyQualifiedName~WatchRegistryTests"`
  Expected: PASS (6 tests green).

- [ ] **Step 5: Full headless gate + commit.**

  Run: `dotnet test -c Release --filter "Category!=Desktop"`
  Expected: `Failed: 0` (all prior + new watch tests green).

```bash
git add src/FlaUI.Mcp.Core/Watch/WatchRegistry.cs test/FlaUI.Mcp.Tests/Watch/WatchRegistryTests.cs
git commit -m "feat(watch): WatchRegistry caps (TooManyWatches) + idempotent remove + evict-on-close + list"
```

---

## Task 7: `RefRegistry` event-ref layer + `IUiaEventSource` seam + `Uia3EventSource` (PER SPIKE A)

**Files:**
- Modify: `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs` (add the bounded event-ref layer, §16.5)
- Create: `src/FlaUI.Mcp.Core/Watch/IUiaEventSource.cs`
- Create: `src/FlaUI.Mcp.Core/Watch/Uia3EventSource.cs`
- Test: `test/FlaUI.Mcp.Tests/Watch/EventRefLayerTests.cs` (headless — the ref layer only)

**Step 0 — STATE VERIFICATION:** Open `docs/superpowers/spikes/2026-07-03-spikeA-flaui-uia-events.md` (Task 1)
and confirm the FlaUI register/unregister/`CacheRequest` signatures before writing `Uia3EventSource`. If they
differ from the skeleton below in *shape* (arg list, return type, `Activate()`-only cache), STOP and report
`[assumed] -> [actual]` — do NOT edit the skeleton to compile against a guess.

### 7a — Event-ref layer in `RefRegistry` (§16.5, headless-testable)

**Contract:** Event refs must be resolvable by the SAME bare-ref path the agent already uses (§16.5, round-4
Seat O) — so they are registered into `RefRegistry` under the window, but into a **bounded, self-evicting
layer** distinct from the durable snapshot layer: capped at `EventRefCap` (e.g. 64) most-recent per window;
inserting past the cap evicts the oldest event ref (→ later `REF_NOT_FOUND`, expected for a stale async event).
`BeginSnapshot`/`EvictWindow` behavior for the durable layer is unchanged.

- [ ] **Step 1: Write the failing test.**

```csharp
// test/FlaUI.Mcp.Tests/Watch/EventRefLayerTests.cs
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Perception;
using FlaUI.Core.Definitions;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class EventRefLayerTests
{
    private static ElementDescriptor Desc() =>
        new(new[] { 1, 2 }, ControlType.Edit, "aid", "name", null, System.Array.Empty<int>());

    [Fact]
    public void Event_ref_is_resolvable_by_lookup_like_a_normal_ref()
    {
        var r = new RefRegistry();
        var @ref = r.RegisterEventRef("w1", Desc(), cached: null);
        Assert.StartsWith("e", @ref);
        // resolvable via the same descriptor-lookup path the agent's tools use (no throw)
        var d = r.LookupDescriptor("w1", @ref);
        Assert.Equal("aid", d.AutomationId);
    }

    [Fact]
    public void Event_ref_layer_is_bounded_oldest_evicted_past_cap()
    {
        var r = new RefRegistry();
        var first = r.RegisterEventRef("w1", Desc(), null);
        for (int i = 0; i < RefRegistry.EventRefCap; i++) r.RegisterEventRef("w1", Desc(), null);
        // 'first' has been pushed out of the bounded layer -> REF_NOT_FOUND (expected, §4)
        Assert.Throws<ToolException>(() => r.LookupDescriptor("w1", first));
    }

    [Fact]
    public void EvictWindow_also_clears_event_refs()
    {
        var r = new RefRegistry();
        var @ref = r.RegisterEventRef("w1", Desc(), null);
        r.EvictWindow("w1");
        Assert.Throws<ToolException>(() => r.LookupDescriptor("w1", @ref));
    }

    [Fact]
    public void Event_ref_survives_BeginSnapshot_of_same_window()
    {
        // §16.5: the event-ref layer is DISTINCT from the durable snapshot layer. A desktop_snapshot
        // (BeginSnapshot replaces the window's durable ref map) must NOT wipe event refs — else the agent's
        // natural reaction to structure_changed (re-snapshot) would kill the ref it was handed.
        var r = new RefRegistry();
        var @ref = r.RegisterEventRef("w1", Desc(), null);
        r.BeginSnapshot("w1");                       // wipes the DURABLE layer only
        var d = r.LookupDescriptor("w1", @ref);      // event ref still resolvable
        Assert.Equal("aid", d.AutomationId);
    }
}
```

> **Note on `LookupDescriptor`:** `RefRegistry.Lookup` is currently `internal` and returns an internal `Entry`
> (`RefRegistry.cs:68`). Add a thin `public ElementDescriptor LookupDescriptor(string windowId, string @ref)`
> that returns `Lookup(windowId, @ref).Descriptor` — what the test and the agent tool path use. Do NOT change
> the existing `Lookup`/`Resolve` signatures.

- [ ] **Step 2: Run to verify failure.**

  Run: `dotnet test --filter "FullyQualifiedName~EventRefLayerTests"`
  Expected: FAIL to compile (`RegisterEventRef`, `LookupDescriptor`, `EventRefCap` not defined).

- [ ] **Step 3: Add the event-ref layer to `RefRegistry`.** In `src/FlaUI.Mcp.Core/Perception/RefRegistry.cs`:
  - Add `public const int EventRefCap = 64;`.
  - Add a **SEPARATE** per-window store for event-ref entries — **NOT `_byWindow`** — plus its insertion order:
    `Dictionary<string, Dictionary<string, Entry>> _eventByWindow` and
    `Dictionary<string, LinkedList<string>> _eventOrder`. **Why separate (spec §16.5 — load-bearing):** the
    event-ref layer must be **DISTINCT from the durable snapshot layer and persist until window close**.
    `BeginSnapshot` (`RefRegistry.cs:22-31`) REPLACES `_byWindow[windowId]` on every `desktop_snapshot`; if event
    refs lived there, any snapshot the agent takes (its natural reaction to a `structure_changed`) would wipe all
    its event refs — violating §16.5. Storing them separately means `BeginSnapshot` leaves them intact.
  - Add `public string RegisterEventRef(string windowId, ElementDescriptor descriptor, AutomationElement? cached)`
    that mints from the SAME never-reused `_counter` (so an event ref’s `e{n}` can never alias a durable ref —
    follow the `Register` idiom at `RefRegistry.cs:52-64`), stores the `Entry` in `_eventByWindow[windowId]`, and
    appends the ref to `_eventOrder[windowId]`; when that list exceeds `EventRefCap`, remove the oldest ref from
    BOTH `_eventOrder[windowId]` and `_eventByWindow[windowId]`.
  - Make event refs resolvable via the SAME path the agent’s tools use (§16.5): in `Lookup`
    (`RefRegistry.cs:68-78`) add a fallback — if the ref is not in `_byWindow[windowId]`, check
    `_eventByWindow[windowId]` before throwing `REF_NOT_FOUND`. (Additive — the durable path is unchanged; this
    only adds a second place to find a ref on a miss. `Resolve` calls `Lookup`, so event refs become resolvable
    through `RunOnRefAsync`/`RunOnRefReadAsync` too.)
  - Extend `EvictWindow` (`RefRegistry.cs:41-49`) to also remove the window from `_eventByWindow` and
    `_eventOrder`.
  - Add `public ElementDescriptor LookupDescriptor(string windowId, string @ref) => Lookup(windowId, @ref).Descriptor;`.
  - All mutation stays under the existing `_gate` lock.

- [ ] **Step 4: Run to verify pass.**

  Run: `dotnet test --filter "FullyQualifiedName~EventRefLayerTests"`
  Expected: PASS (3 tests green). Also run `dotnet test --filter "FullyQualifiedName~RefRegistryTests"` →
  existing durable-ref tests still PASS (no regression).

### 7b — `IUiaEventSource` seam + `Uia3EventSource` (spike-gated UIA — no headless test; Desktop-tested in Task 10)

- [ ] **Step 5: Define the seam.**

```csharp
// src/FlaUI.Mcp.Core/Watch/IUiaEventSource.cs
using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>What one subscription needs registered on the query STA. WindowId = the top-level window handle id;
/// WindowProcessId = its cached PID (for the §7 filter); ScopeRef = the subscribed structure_changed scope ref
/// (null = window root); CoalesceScope = the string used as the structure_changed coalesce key part (§8).</summary>
public sealed record WatchSubscriptionSpec(
    string SubscriptionId, string WindowId, int WindowProcessId,
    IReadOnlyList<WatchEventKind> Kinds, string? ScopeRef, string CoalesceScope);

/// <summary>The raw UIA-side event delivery. Register returns a disposable that unregisters EXACTLY this
/// subscription's handlers (ref-counting global registrations internally, §7). The callback fires on a COM
/// RPC thread: it MUST read only CACHED props and MUST NOT throw (§6/§16.4). Implemented by Uia3EventSource
/// on the query STA; faked in future headless tests.</summary>
public interface IUiaEventSource
{
    /// <param name="onCapture">invoked on the COM thread with the plain-data meta AND an opaque source token
    /// the payload-build later resolves on the STA (null for window_closed). MUST NOT block.</param>
    IDisposable Register(WatchSubscriptionSpec spec, Action<CapturedEventMeta, object?> onCapture);
}
```

- [ ] **Step 6: Implement `Uia3EventSource` PER SPIKE A.** Create `src/FlaUI.Mcp.Core/Watch/Uia3EventSource.cs`.
  It takes the `AutomationDispatcher`/`WindowManager` (to run registration on the query STA) and the
  `UIA3Automation` root. For each subscription, on the query STA (`RunQueryAsync`):
  - Build a `CacheRequest` caching **only** `ProcessId` + `RuntimeId` (§6, §16.1) — **as a Register\* param if
    Spike A found that overload; otherwise `Activate()` inside a `using` scoped to the single registration
    call** (§16.1 — never leak the active cache on the shared STA).
  - Register per kind PER SPIKE A: `window_opened`/`window_closed` **globally on the Desktop root**,
    `focus_changed` via `RegisterFocusChangedEvent` (root), `structure_changed` via
    `RegisterStructureChangedEvent(Subtree)` on the window root or the `ScopeRef` element.
  - **Transactional (§16.2):** collect each registration’s disposable; if ANY throws, dispose all already-added
    and rethrow — never orphan a live handler.
  - **Ref-count global registrations (§7):** `focus_changed`, global `window_opened`, global `window_closed`
    each established once process-wide and shared; the last subscription needing one removes it.
  - In each handler (COM thread), read the **cached** `ProcessId`/`RuntimeId` (fail-soft; missing → treat as
    undeliverable, §16.4), build a `CapturedEventMeta` (join RuntimeId as `"a,b,c"`, `""` if unreadable), and
    call `onCapture(meta, sourceElementOrNull)` — the source token is the FlaUI source element for later STA
    resolution (null for `window_closed`). NEVER throw on the callback thread.
  - Return an `IDisposable` that unregisters exactly this subscription’s per-kind handlers and decrements the
    global ref-counts. **CRITICAL — the returned disposable MUST self-marshal its unregistration onto the query
    STA** (`dispatcher.RunQueryAsync(() => { RemoveAutomationEventHandler(...); ... })`, awaited/blocked inside
    `Dispose`). UIA is apartment-affine and the handlers were registered on the query STA, so a `Dispose` called
    from an MTA thread (an MCP tool handler running `UnwatchAsync`, or the off-STA `WindowInvalidated` sweep —
    §9) would otherwise throw `RPC_E_WRONG_THREAD`. Self-marshaling lets any caller `Dispose` from any thread
    safely (this mirrors Phase-6’s `PerceptionManager.cs:38` `PostToQuerySta` eviction marshal). Guard the
    marshaled unregister in try/catch — the handler may already be dead when a window closed.

  Mark every FlaUI call site with `// PER SPIKE A: <exact signature from findings note>`. If the found API
  differs in shape, STOP and report before adapting.

- [ ] **Step 7: Build (no headless test for the UIA impl — it is Desktop-tested in Task 10).**

  Run: `dotnet build -c Release`
  Expected: build succeeds, 0 errors.

- [ ] **Step 8: Commit.**

```bash
git add src/FlaUI.Mcp.Core/Perception/RefRegistry.cs src/FlaUI.Mcp.Core/Watch/IUiaEventSource.cs \
        src/FlaUI.Mcp.Core/Watch/Uia3EventSource.cs test/FlaUI.Mcp.Tests/Watch/EventRefLayerTests.cs
git commit -m "feat(watch): bounded event-ref layer + IUiaEventSource seam + Uia3EventSource (per Spike A)"
```

---

## Task 8: `WatchService` + `WatchPump` + `IEventSink` — the pipeline (PER SPIKE B)

**Files:**
- Create: `src/FlaUI.Mcp.Core/Watch/IEventSink.cs`
- Create: `src/FlaUI.Mcp.Core/Watch/WatchService.cs`
- Create: `src/FlaUI.Mcp.Core/Watch/WatchPump.cs`

**Step 0 — STATE VERIFICATION:** Open `docs/superpowers/spikes/2026-07-03-spikeB-mcp-notification.md` and read
the **DELIVERY DECISION**. If **push-only** or **push+drain**, build the emit path below. If **drain-only**,
the pump buffers per-subscription instead of emitting and Task 9 ships `desktop_drain_events` (see the inline
DRAIN-MODE note). Confirm the decision before writing the pump.

**Contract:**
- `IEventSink` (Core): `Task EmitAsync(DesktopEventPayload payload, CancellationToken ct)`. Server implements it
  over `IMcpServer.SendNotificationAsync` (Task 9). Keeping it a Core seam keeps `WatchPump` headless-shaped and
  SDK-free.
- `WatchService` (Core): the façade the tools call. `WatchAsync(windowId, kinds, scopeRef, timeoutMs)` resolves
  the window (`WindowHandleStale`), applies the deny-list (§10 `TargetDenied` via `PerceptionPolicy.IsDenied` —
  mirror `PerceptionManager.FindAsync` guards at `PerceptionManager.cs:227-241`), resolves `scopeRef`
  (`RefStaleUnresolvable`) and reads the window’s cached PID **on the query STA**, then `registry.Create(...)`
  (TooManyWatches propagates) → `source.Register(spec, onCapture)` transactionally (on failure
  `registry.Remove(id)` + rethrow) → store the disposable. `onCapture` applies §7 `FocusEventFilter` (drop
  foreign/PID-less BEFORE the channel) then writes an `EventEnvelope(meta, source, coalesceScope)` to the
  **bounded channel** (`Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(capacity) { FullMode =
  BoundedChannelFullMode.DropWrite, SingleReader = true })` — the COM thread never blocks; a dropped write bumps
  the sub’s droppedCount). `UnwatchAsync(id)` disposes the sub’s registration (safe from any thread — the
  disposable self-marshals onto the query STA, Task 7 Step 6) + `registry.Remove`. `OnWindowInvalidated(windowId)`
  disposes+drops all its subs (again via the self-marshaling disposable — this callback fires OFF the query STA
  on a ThreadPool thread, so the self-marshal is what makes the UIA unregister safe) and, for any sub that had
  `window_closed`, enqueues a synthetic `window_closed` envelope (null source) so the agent gets a final close
  (§9). `List()` delegates to the registry. **Teardown ownership (§9):** because `WatchService` HOLDS the
  registration disposables, it — not `WatchPump` — owns disposing them: add `ValueTask DisposeAllAsync()` (or
  `StopAsync`) on `WatchService` that disposes every held registration (each self-marshals) and clears them. The
  hosted service (Task 9) calls this on shutdown. Do NOT put registration teardown in `WatchPump` (the `Channel`
  decouples them — the pump cannot reach `WatchService`’s registration map).
- `WatchPump` (Core): **Constructor DI (enumerate ALL):** the shared `Channel<EventEnvelope>` (read side),
  `AutomationDispatcher` (or `WindowManager`) for the query-STA marshal, **`RefRegistry`** (to `RegisterEventRef`
  and to resolve the structure_changed scope element on the STA), `WatchRegistry` (to bump droppedCount), and
  `IEventSink`. (The `EventCoalescer` is NOT injected — the pump constructs and OWNS its own
  `new EventCoalescer(256, 100)` internally, since it is worker-confined and shared with nothing, §11.
  `PerceptionManager` is not required either — `RefRegistry`+`WindowManager` cover ref resolution + STA roots.)
  `StartAsync`/`StopAsync`. One long-running loop reads envelopes, `coalescer.Offer(...)`
  (bump droppedCount on the returned sub), and on a short **time-driven** cadence `coalescer.Drain(now)`.
  **CRITICAL liveness (do NOT `await ReadAsync`/`await foreach` alone):** `Drain` is TIME-based — a
  `structure_changed` aggregate becomes ready only after `debounceMs` of quiet. A loop that blocks purely on the
  next channel item would leave the final settled `structure_changed` un-emitted until some UNRELATED event
  happens to wake the reader (an indefinite hang; it would make the Task-10 `Structure_changed_coalesces` test
  time out). So the loop must wake to `Drain(now)` on a timer when items are settling — **but the timer MUST be
  CONDITIONAL, not an unconditional `PeriodicTimer` that spins for the server's whole lifetime** (with zero
  watches / an empty coalescer that would be a permanent idle-wake power drain). Pattern: when the coalescer has
  NO pending items, block indefinitely on `await reader.WaitToReadAsync(ct)` (idle = truly asleep); when it DOES
  hold unsettled `structure_changed` aggregates, race `WaitToReadAsync(ct)` against a short
  `Task.Delay(~debounceMs/2, ct)` so the loop wakes to `Drain(now)` on settle even if no new envelope arrives.
  (`Drain`/`Offer` stay single-threaded — only this worker touches the coalescer; §11.) For each ready aggregate
  it marshals a **payload-build onto the query STA**
  (`dispatcher.RunQueryAsync`) — wrapping the
  correct element in a live `IEventSourceReader` and building the payload via `WatchPayloadBuilder.Build`.
  **§8/R5 ref rule (which element the reader wraps):** for `focus_changed`/`window_opened` the reader wraps the
  event SOURCE element (mint the source’s ref). For `structure_changed` the reader wraps the **subscribed SCOPE**
  element (the window root, or the `scope` ref element — resolve it on the STA from the envelope’s
  `CoalesceScope`/sub), so `MintRef()` returns the SCOPE’s ref, NOT the transient last-collapsed child (which may
  already be gone and would be useless to snapshot — spec §8/R5). For `window_closed` there is no source →
  `HasSource=false` reader (null ref). The reader reads controlType/name(redacted via
  `RedactionPolicy.IsPasswordOrFailClosed`)/bounds and mints the event ref via `RefRegistry.RegisterEventRef`.
  **Per-event deny-list re-check (defense-in-depth, §10):** during the STA build, for events WITH a source
  (`HasSource==true`), run the process deny-list on the source PID
  (`PerceptionPolicy.IsDenied(SafeProcessName(...))`, mirroring `PerceptionManager.EnsureAllowed` at
  `PerceptionManager.cs:105`, which the codebase already runs on EVERY targeted read) and DROP the event if
  denied. **SKIP this check entirely for `HasSource==false` (synthetic `window_closed`)** — the window/process
  is already gone (its PID may be dead), it was authorized at subscribe time, and there is no source to
  re-evaluate; running it would at best resolve to a null process name (`SafeProcessName` returns null on a dead
  PID → `IsDenied(null)` false, not a throw) and at worst risk swallowing the final `window_closed` the agent
  most needs. NB: the deny-list is **process-coarse** and the watched PID is
  fixed for the window’s lifetime, so subscribe-time deny already covers the normal case — this per-event check
  is cheap belt-and-suspenders for PID edge cases, NOT a fix for an in-process navigation to a credential
  surface (that is the documented process-coarse limitation, backstopped field-by-field by the per-event INV-5
  `IsPassword` redaction the builder already applies).
  Then, **off the STA**, `await sink.EmitAsync(...)`. Per-item `try/catch` log-and-continue (§16.3); a
  `SendNotificationAsync` failure (client gone) is terminal — stop the pump cleanly. `StopAsync` completes the
  channel and cancels the loop ONLY (registration teardown belongs to `WatchService.DisposeAllAsync`, above).

Because this task’s correctness is dominated by STA marshaling + real UIA + the SDK sink, it is validated by
the **Task 10 Desktop tests** with a recording `IEventSink`, not by new headless units. (The pure pieces it
composes — coalescer, filter, builder, registry, ref-layer — are already unit-covered.)

- [ ] **Step 1: Define `IEventSink` and the envelope.**

```csharp
// src/FlaUI.Mcp.Core/Watch/IEventSink.cs
using System.Threading;
using System.Threading.Tasks;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>Emit one built payload to the client. Server implements this over IMcpServer.SendNotificationAsync
/// (keeps WatchPump SDK-free). An emit failure signals the session is gone (pump stops cleanly).</summary>
public interface IEventSink
{
    Task EmitAsync(DesktopEventPayload payload, CancellationToken ct);
}

/// <summary>Channel item: the plain meta + the opaque source token (a FlaUI AutomationElement or null),
/// resolved to properties only on the query STA during payload-build. CoalesceScope is the structure_changed
/// key part (§8) carried from the subscription.</summary>
public readonly record struct EventEnvelope(CapturedEventMeta Meta, object? Source, string CoalesceScope);
```

- [ ] **Step 2: Implement `WatchService`** per the contract above. **Constructor DI (enumerate ALL — an
  implementer following this list must have everything the resolution rules need):** `WindowManager`,
  `WatchRegistry`, `IUiaEventSource`, the shared `Channel<EventEnvelope>` (write side), and **`RefRegistry`** —
  the last is required to resolve `scopeRef` (`e{n}` → element) at subscribe time via
  `RefRegistry.Resolve(windowId, scopeRef, PopupFinder.SearchRoots(win, desktop))` inside a
  `WindowManager.RunWithWindowAndDesktopAsync` (mirrors `PerceptionManager.FindAsync`’s scope resolution at
  `PerceptionManager.cs:239-241`). (`PerceptionManager` itself is NOT needed — `RefRegistry`+`WindowManager`
  suffice; the deny-list uses static `PerceptionPolicy.IsDenied`.) Subscribe to `WindowManager.WindowInvalidated` in
  the constructor to drive `OnWindowInvalidated` (mirror `PerceptionManager.cs:38` lifetime reasoning). Hold each
  sub’s `IDisposable` registration keyed by subscriptionId. Expose `WatchAsync`, `UnwatchAsync`, `List`.
  `WatchAsync` returns a small `record WatchHandle(string Id, IReadOnlyList<WatchEventKind> Kinds)`.

- [ ] **Step 3: Implement `WatchPump`** per the contract above (or the DRAIN-MODE variant below).

  > **DRAIN-MODE (only if Spike B decision = drain-only):** replace the `sink.EmitAsync` step with an append to a
  > bounded per-subscription buffer that `WatchService` owns; the event ref minted during payload-build MUST be
  > pinned alive until drained (§16.5 round-4 Seat P — do NOT let a TTL expire a buffered ref before the agent
  > polls). Task 9 adds `desktop_drain_events(subscriptionId, max?)` returning + clearing buffered payloads.

- [ ] **Step 4: Build.**

  Run: `dotnet build -c Release`
  Expected: build succeeds, 0 errors.

- [ ] **Step 5: Commit.**

```bash
git add src/FlaUI.Mcp.Core/Watch/IEventSink.cs src/FlaUI.Mcp.Core/Watch/WatchService.cs \
        src/FlaUI.Mcp.Core/Watch/WatchPump.cs
git commit -m "feat(watch): WatchService pipeline + WatchPump worker + IEventSink seam (per Spike B)"
```

---

## Task 9: Server wiring — `McpEventSink`, hosted-service pump, DI (PER SPIKE B)

**Files:**
- Create: `src/FlaUI.Mcp.Server/Watch/McpEventSink.cs`
- Create: `src/FlaUI.Mcp.Server/Watch/WatchPumpHostedService.cs`
- Modify: `src/FlaUI.Mcp.Server/Program.cs`

**Step 0 — STATE VERIFICATION:** From the Spike B note, confirm the exact `IMcpServer` acquisition mechanism
and `SendNotificationAsync` overload before writing `McpEventSink`. If the injection point differs from the
skeleton, STOP and report.

- [ ] **Step 1: Implement `McpEventSink` PER SPIKE B.** `IEventSink` over the SDK. It needs the session
  `IMcpServer`; acquire it by the mechanism Spike B confirmed (DI singleton captured at host start, or a
  server reference stashed by the pump host service). `EmitAsync` calls
  `SendNotificationAsync("notifications/flaui/desktop_event", payload, ct)` with the STJ serialization the
  spike confirmed (the payload record’s explicit `JsonPropertyName`s make the wire keys deterministic).

- [ ] **Step 2: Implement `WatchPumpHostedService : IHostedService`** — a thin wrapper: `StartAsync` starts the
  Core `WatchPump`; `StopAsync` calls BOTH `WatchPump.StopAsync` (complete channel + stop worker) AND
  `WatchService.DisposeAllAsync` (dispose every held UIA registration, each self-marshaling onto the query STA —
  §9 connection-teardown). Order: stop the pump first (no more builds/emits), then dispose registrations. If
  Spike B’s mechanism requires capturing `IMcpServer` at start, do it here.

- [ ] **Step 3: Wire DI in `Program.cs`.** After the existing perception singletons (around `Program.cs:33`,
  where `PerceptionManager` is registered) and before `.AddMcpServer()` (`Program.cs:63`), add:

```csharp
// --- Phase 8 desktop_watch (UIA event streaming over stdio) ---
builder.Services.AddSingleton(_ =>
    System.Threading.Channels.Channel.CreateBounded<FlaUI.Mcp.Core.Watch.EventEnvelope>(
        new System.Threading.Channels.BoundedChannelOptions(256)
        { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite, SingleReader = true }));
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WatchRegistry>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.IUiaEventSource, FlaUI.Mcp.Core.Watch.Uia3EventSource>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.IEventSink, FlaUI.Mcp.Server.Watch.McpEventSink>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WatchService>();
builder.Services.AddSingleton<FlaUI.Mcp.Core.Watch.WatchPump>();
builder.Services.AddHostedService<FlaUI.Mcp.Server.Watch.WatchPumpHostedService>();
// NOTE: WatchTools is registered in Task 10 — the class does not exist yet, so registering it here would
// break this task's `dotnet build`. Do NOT add it in this task.
```

  > Match the exact constructor dependencies of `Uia3EventSource`/`WatchService`/`WatchPump` from Tasks 7–8.
  > `WatchService` (writer) and `WatchPump` (reader) share the single `Channel<EventEnvelope>` singleton — inject
  > `.Writer`/`.Reader` off it, or the channel itself. If the SDK cannot inject `IMcpServer` at DI time, resolve
  > it in the hosted service `StartAsync` and set it on `McpEventSink` (Spike B decides).

- [ ] **Step 4: Build + full headless gate.**

  Run: `dotnet build -c Release && dotnet test -c Release --filter "Category!=Desktop"`
  Expected: build 0 errors; `Failed: 0`.

- [ ] **Step 5: Commit.**

```bash
git add src/FlaUI.Mcp.Server/Watch/McpEventSink.cs src/FlaUI.Mcp.Server/Watch/WatchPumpHostedService.cs \
        src/FlaUI.Mcp.Server/Program.cs
git commit -m "feat(watch): McpEventSink over IMcpServer + hosted-service pump + DI wiring (per Spike B)"
```

---

## Task 10: `WatchTools` — the three MCP tools + Desktop tests

**Files:**
- Create: `src/FlaUI.Mcp.Server/Tools/WatchTools.cs`
- Test: `test/FlaUI.Mcp.Tests/Watch/DesktopWatchTests.cs` (`Category=Desktop`)

**Tool contract (§3):** all three are `[McpServerTool(ReadOnly = true)]`, routed through `ToolResponse.Guard`
(NOT `GuardWrite` — they synthesize no input and are lease-exempt). Model the file on `FindTools.cs` (ctor DI,
`[Description]` per param, return `ToolResponse.Guard(async () => ToolResponse.Ok(new { ... }))`).

- [ ] **Step 1: Implement `WatchTools`.**

```csharp
// src/FlaUI.Mcp.Server/Tools/WatchTools.cs
using System.ComponentModel;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Watch;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Tools;

[McpServerToolType]
public sealed class WatchTools
{
    private readonly WatchService _watch;
    public WatchTools(WatchService watch) => _watch = watch;

    [McpServerTool(ReadOnly = true), Description(
        "Subscribe to UIA events on a window and receive them as async MCP notifications " +
        "(method 'notifications/flaui/desktop_event') instead of polling snapshots. events is one+ of: " +
        "window_opened, window_closed (child dialogs/popups of this window), focus_changed (input focus " +
        "moves within this window's process), structure_changed (subtree repopulated - coalesced/debounced). " +
        "Optional scope=<a live ref> narrows structure_changed to that element's subtree. Returns " +
        "{subscriptionId, window, events, scope?}. Each notification payload is " +
        "{subscriptionId, event, window, ref?, controlType?, name?, bounds?, coalescedCount, timestampUtc} " +
        "(name is [REDACTED] for password fields; ref/name/bounds may be absent, e.g. window_closed). The " +
        "payload 'ref' is EPHEMERAL - minted into a small bounded pool, so it returns REF_NOT_FOUND if you wait " +
        "too long to act; re-desktop_snapshot for a durable ref. ReadOnly + lease-exempt. NOTE: your OWN " +
        "desktop_type/click/key calls fire events too - events right after your input are likely self-caused " +
        "(correlate by timing). Caps: 5 watches/window, 20/session (TooManyWatches).")]
    public Task<string> DesktopWatch(
        [Description("Window handle, e.g. w1.")] string window,
        [Description("Event kinds: window_opened, window_closed, focus_changed, structure_changed.")] string[] events,
        [Description("Optional live ref to scope structure_changed to a subtree.")] string? scope = null,
        [Description("Registration timeout in ms (default 4000).")] int timeoutMs = 4000)
        => ToolResponse.Guard(async () =>
        {
            if (events is null || events.Length == 0)
                throw new ToolException(ToolErrorCode.InvalidArguments,
                    "events must contain at least one event kind.",
                    "pass e.g. [\"window_opened\",\"structure_changed\"]");
            var kinds = new List<WatchEventKind>();
            foreach (var t in events)
            {
                if (!WatchEventKinds.TryParse(t, out var k))
                    throw new ToolException(ToolErrorCode.InvalidArguments,
                        $"Unknown event kind '{t}'.",
                        "use window_opened, window_closed, focus_changed, or structure_changed");
                if (!kinds.Contains(k)) kinds.Add(k);
            }
            var sub = await _watch.WatchAsync(window, kinds, scope, timeoutMs);
            return ToolResponse.Ok(new { subscriptionId = sub.Id, window, events = sub.Kinds.Select(WatchEventKinds.ToWire), scope });
        });

    [McpServerTool(ReadOnly = true), Description(
        "Stop a subscription created by desktop_watch. Idempotent: an unknown/already-ended subscriptionId " +
        "returns ok:true (your intent - no more events - is satisfied).")]
    public Task<string> DesktopUnwatch(
        [Description("The subscriptionId from desktop_watch, e.g. s1.")] string subscriptionId)
        => ToolResponse.Guard(async () =>
        {
            await _watch.UnwatchAsync(subscriptionId);
            return ToolResponse.Ok(new { ok = true, subscriptionId });
        });

    [McpServerTool(ReadOnly = true), Description(
        "List your active watch subscriptions (recover them after a context loss). Returns " +
        "watches[{subscriptionId, window, events, scope?, droppedCount}] - droppedCount>0 means some events " +
        "were coalesced-dropped under load (re-snapshot to resync).")]
    public Task<string> DesktopListWatches()
        => ToolResponse.Guard(() =>
        {
            var watches = _watch.List().Select(w => new
            {
                subscriptionId = w.SubscriptionId, window = w.WindowId,
                events = w.Kinds.Select(WatchEventKinds.ToWire), scope = w.Scope, droppedCount = w.DroppedCount
            });
            return Task.FromResult(ToolResponse.Ok(new { watches }));
        });
}
```

  > `WatchService.WatchAsync` must enforce, BEFORE any UIA registration, the deny-list (`TargetDenied`),
  > stale-handle (`WindowHandleStale`), and stale-`scope` (`RefStaleUnresolvable`) refusals — reuse
  > `PerceptionPolicy.IsDenied` and existing ref resolution (Task 8 Step 2). These refusals surface here through
  > `ToolResponse.Guard`’s `ToolException` mapping.

  Now register the tool in DI (deferred here from Task 9 because the class only exists now — mirrors every other
  tool singleton in `Program.cs:29-61`). In `Program.cs`, alongside the Phase-8 block from Task 9 Step 3, add:

```csharp
builder.Services.AddSingleton<FlaUI.Mcp.Server.Tools.WatchTools>();
```

- [ ] **Step 2: Write the Desktop tests** (`[Trait("Category","Desktop")]`), modeling `OpenAsync` on
  `FindTests.cs:14-22` but constructing the full watch pipeline with a **recording `IEventSink`**:

```csharp
// test/FlaUI.Mcp.Tests/Watch/DesktopWatchTests.cs
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Threading;
using FlaUI.Mcp.Core.Watch;
using FlaUI.Mcp.Core.Windows;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

[Trait("Category", "Desktop")]
public class DesktopWatchTests
{
    private sealed class RecordingSink : IEventSink
    {
        public ConcurrentQueue<DesktopEventPayload> Events { get; } = new();
        public Task EmitAsync(DesktopEventPayload payload, CancellationToken ct)
        { Events.Enqueue(payload); return Task.CompletedTask; }
    }

    private static async Task<bool> WaitFor(RecordingSink sink, Func<DesktopEventPayload, bool> pred, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (sink.Events.Any(pred)) return true;
            await Task.Delay(50);
        }
        return false;
    }

    [Fact]
    public async Task Window_opened_fires_when_child_dialog_opens()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (svc, sink, handle, mgr, pump) = await BuildAsync(app, dispatcher);
        await using var _ = pump;
        using (mgr)
        {
            var sub = await svc.WatchAsync(handle.Id, new[] { WatchEventKind.WindowOpened }, null, 4000);
            // click ModalButton -> opens a child "Modal" window (MainWindow.xaml.cs:49 ModalButton_Click)
            await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            { win.FindFirstDescendant(cf => cf.ByAutomationId("ModalButton"))!.AsButton().Invoke(); return true; });

            Assert.True(await WaitFor(sink, e => e.Event == "window_opened" && e.SubscriptionId == sub.Id));
        }
    }

    [Fact]
    public async Task Structure_changed_coalesces_a_rebuild_into_one_event()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (svc, sink, handle, mgr, pump) = await BuildAsync(app, dispatcher);
        await using var _ = pump;
        using (mgr)
        {
            var sub = await svc.WatchAsync(handle.Id, new[] { WatchEventKind.StructureChanged }, null, 4000);
            await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            { win.FindFirstDescendant(cf => cf.ByAutomationId("RebuildItemsButton"))!.AsButton().Invoke(); return true; });

            Assert.True(await WaitFor(sink, e => e.Event == "structure_changed" && e.SubscriptionId == sub.Id));
            await Task.Delay(400); // let debounce settle any stragglers
            var count = sink.Events.Count(e => e.Event == "structure_changed" && e.SubscriptionId == sub.Id);
            Assert.True(count <= 2, $"expected coalesced structure_changed (<=2), got {count}");
        }
    }

    [Fact]
    public async Task Unwatch_stops_delivery()
    {
        using var app = new TestAppFixture();
        using var dispatcher = new AutomationDispatcher();
        var (svc, sink, handle, mgr, pump) = await BuildAsync(app, dispatcher);
        await using var _ = pump;
        using (mgr)
        {
            var sub = await svc.WatchAsync(handle.Id, new[] { WatchEventKind.StructureChanged }, null, 4000);
            await svc.UnwatchAsync(sub.Id);
            await Task.Delay(100);
            sink.Events.Clear();
            await mgr.RunWithWindowAndDesktopAsync(handle, (win, _) =>
            { win.FindFirstDescendant(cf => cf.ByAutomationId("RebuildItemsButton"))!.AsButton().Invoke(); return true; });
            await Task.Delay(500);
            Assert.DoesNotContain(sink.Events, e => e.SubscriptionId == sub.Id);
        }
    }

    // BuildAsync(app, dispatcher): construct WindowManager, RefRegistry, PerceptionManager, WatchRegistry,
    // Uia3EventSource, a bounded Channel<EventEnvelope>, EventCoalescer, RecordingSink, WatchPump (started),
    // WatchService; open the TestApp window; return the tuple. Model construction on FindTests.OpenAsync
    // (FindTests.cs:14-22) plus the DI shapes from Task 8/9. 'pump' exposes StopAsync via IAsyncDisposable.
    private static Task<(WatchService svc, RecordingSink sink, WindowHandle handle, WindowManager mgr, IAsyncDisposable pump)>
        BuildAsync(TestAppFixture app, AutomationDispatcher dispatcher) => /* implement per Task 8/9 wiring */ throw new NotImplementedException();
}
```

  Fill `BuildAsync` with the real construction (per the Task 8/9 constructor shapes). The three asserts are the
  oracle for the whole pipeline.

- [ ] **Step 3: Run the Desktop tests (interactive session).**

  Run: `dotnet test --filter "Category=Desktop&FullyQualifiedName~DesktopWatchTests"`
  Expected: PASS (3 tests) — window_opened arrives, structure_changed is coalesced (≤2), unwatch stops delivery.

  > If push delivery to a real client was **drain-only** in Spike B, these tests still validate the pipeline via
  > the recording sink (the sink is downstream of the same build/emit path). Add a `desktop_drain_events` tool +
  > one drain test here per the Task 8 DRAIN-MODE note.

- [ ] **Step 4: Full headless gate (ensure no regression) + commit.**

  Run: `dotnet test -c Release --filter "Category!=Desktop"`
  Expected: `Failed: 0`.

```bash
git add src/FlaUI.Mcp.Server/Tools/WatchTools.cs test/FlaUI.Mcp.Tests/Watch/DesktopWatchTests.cs
git commit -m "feat(watch): desktop_watch/unwatch/list_watches tools + Desktop event pipeline tests"
```

---

## Task 11: Version bump + docs (v0.7.7 → v0.8.0)

**Files:**
- Modify: `src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj` (line 20)
- Modify: `installer/flaui-mcp.iss` (line 4)
- Modify: `CHANGELOG.md` (new top section)
- Modify: `ROADMAP.md` (Phase 8 → shipped; v2 table row)
- Modify: `README.md` (new Event streaming section)
- Modify: `.claude/skills/driving-flaui-mcp/SKILL.md` (react-instead-of-poll note + self-trigger warning)

- [ ] **Step 1: Bump versions.** In `FlaUI.Mcp.Server.csproj` change `<Version>0.7.7</Version>` →
  `<Version>0.8.0</Version>`. In `installer/flaui-mcp.iss` change `#define AppVersion "0.7.7"` →
  `#define AppVersion "0.8.0"`.

- [ ] **Step 2: CHANGELOG.** Insert a new section above `## [0.7.7] - 2026-07-03`:

```markdown
## [0.8.0] - 2026-07-03

### Added
- **`desktop_watch` / `desktop_unwatch` / `desktop_list_watches`** — push perception: subscribe to UIA
  events (`window_opened`, `window_closed`, `focus_changed`, `structure_changed`) and receive them as MCP
  server→client notifications (`notifications/flaui/desktop_event`) over the existing **stdio** pipe — no
  HTTP/SSE. All three are ReadOnly and lease-exempt. Events carry a freshly-minted (bounded, evictable) `ref`,
  `controlType`, INV-5-redacted `name`, `bounds`, and a `coalescedCount`. `structure_changed` is coalesced +
  debounced; focus/window events are process-filtered to the subscribed window. Subscriptions auto-evict when
  their window closes (reuses the Phase-6 `WindowInvalidated` chokepoint). Caps: 5 watches/window, 20/session.
```

- [ ] **Step 3: ROADMAP.** Change the Phase 8 line (`ROADMAP.md:155`) from `🔵 **(design; target v0.8.0)**` to
  `✅ **(shipped v0.8.0)**` and tighten the prose to past tense. In the v2 table, the **UIA event streaming** row
  (`ROADMAP.md:211`) — change `⬆️ **PULLED INTO Phase 8 (design, v0.8.0)**` → `✅ **SHIPPED in Phase 8 (v0.8.0)**`.

- [ ] **Step 4: README.** Add an "Event streaming (`desktop_watch`)" section near the tool reference: the three
  tools, the notification method name `notifications/flaui/desktop_event`, the payload keys (§4), and the
  best-effort/coalescing/redaction/self-trigger notes. Keep the house style of the existing tool docs. (Repo is
  public — the README-before-push gate applies.)

- [ ] **Step 5: SKILL.md.** Add a "React instead of poll" note to `.claude/skills/driving-flaui-mcp/SKILL.md`:
  `desktop_watch` → handle the `notifications/flaui/desktop_event` → `desktop_snapshot`/`get_text` the event
  `ref` for detail; warn that events right after your own `desktop_type`/`click`/`key` are likely self-caused
  (§16.6); note `ref`s are ephemeral (bounded event-ref layer → `REF_NOT_FOUND` when aged out; re-snapshot for a
  durable ref); and that `droppedCount` from `desktop_list_watches` signals coalescing loss (re-snapshot to
  resync).

- [ ] **Step 6: Build + full headless gate.**

  Run: `dotnet build -c Release && dotnet test -c Release --filter "Category!=Desktop"`
  Expected: build 0 errors; `Failed: 0`.

- [ ] **Step 7: Commit.**

```bash
git add src/FlaUI.Mcp.Server/FlaUI.Mcp.Server.csproj installer/flaui-mcp.iss CHANGELOG.md ROADMAP.md \
        README.md .claude/skills/driving-flaui-mcp/SKILL.md
git commit -m "docs+release: v0.8.0 desktop_watch (CHANGELOG, ROADMAP, README, SKILL, version bump)"
```

---

## After all tasks

1. **Dispatch a final whole-branch code review** (ecc:csharp-reviewer) over the full `phase-8-desktop-watch`
   diff — focus on: the COM-callback thread never doing a live UIA read or throwing (§6/§16.4); transactional
   registration + ref-counted global unregister with no orphaned handler (§16.2/§7); the `Activate()`-cache
   never leaking on the shared query STA (§16.1); the pump loop being genuinely fault-proof (§16.3); event refs
   bounded (§16.5); INV-5 redaction on every payload `name` (§10); and the deny-list/stale guards firing before
   any registration (§10/§3).
2. **AGY-AFTER merge-gate panel** on the finished branch (per the global AGY-AFTER discipline) — fold valid
   findings, verify each by measurement before folding.
3. Then **superpowers:finishing-a-development-branch** (merge decision → cut v0.8.0 on user GO: tag, push,
   release.yml → 4 assets; install + `/mcp` reload + live smoke).

---

## Self-Review (author’s exhaustiveness audit)

**Spec coverage — every spec section maps to a task:**
- §3 tool surface → Task 10 (`WatchTools`); refusals → Task 10 note + Task 6 caps.
- §4 wire contract → Task 3 (`DesktopEventPayload`) + Task 5 (`WatchPayloadBuilder`).
- §5 event kinds → Task 3 (`WatchEventKind`); global-vs-scoped registration → Task 7 (`Uia3EventSource`).
- §6 pipeline → Tasks 7 (capture/cache), 8 (channel/worker/STA-build/emit).
- §7 focus filter → Task 5 (`FocusEventFilter`) + Task 8 (applied before channel).
- §8 back-pressure/coalescing → Task 4 (`EventCoalescer`); kind-dependent key → Task 4 `CoalesceKey`.
- §9 lifecycle → Task 6 (`RemoveByWindow`), Task 8 (`OnWindowInvalidated` + synthetic close), Task 9 (hosted-service StopAsync + WindowInvalidated hook).
- §10 security/redaction → Task 5 (INV-5), Task 10 (deny-list/stale guards before registration).
- §11 single-STA → Tasks 7/8 (register + payload-build on query STA; only new threads = COM callbacks + one worker).
- §13 testing → Tasks 3–6 (headless units), Task 10 (Desktop).
- §14 open questions → Task 1 (FlaUI API), Task 2 (IMcpServer/injection), constants (Task 4/9 defaults), WindowInvalidated payload (Task 8/9 hook — identical Phase-6 `windowId` signal).
- §15a client-delivery risk → Task 2 (host-surfacing spike + decision), drain fallback threaded through Tasks 8/9/10.
- §16.1 CacheRequest-as-param/`Activate` guard → Task 1 Step 3 + Task 7 Step 6.
- §16.2 transactional registration → Task 7 Step 6 + Task 8 Step 2.
- §16.3 bulletproof worker → Task 8 Step 3.
- §16.4 null-tolerant capture → Task 5 (`FocusEventFilter` PID<=0) + Task 7 Step 6.
- §16.5 bounded event-ref layer → Task 7a.
- §16.6 self-trigger doc → Task 10 (tool description) + Task 11 (SKILL.md).
- §15 version/docs → Task 11.

**Type consistency:** `CapturedEventMeta`, `DesktopEventPayload`, `WatchEventKind`/`WatchEventKinds`,
`IEventSourceReader`, `EventEnvelope`, `WatchInfo`, `WatchSubscriptionSpec`, `IUiaEventSource`, `IEventSink`,
`WatchRegistry`/`WatchService`/`WatchPump` are named identically across all tasks that reference them.
`RefRegistry.RegisterEventRef`/`LookupDescriptor`/`EventRefCap` are defined in Task 7a and used in Task 8.

**Known residual gap (flagged, resolved at execution, NOT a placeholder):** the exact FlaUI 5.0.0 event/cache
call sites (Task 7 Step 6) and the `IMcpServer` acquisition (Task 9 Step 1) are deliberately expressed as
contracts marked `// PER SPIKE A/B` — because per PLAN-vs-SPEC discipline they are properties of external APIs
that only the spikes (Tasks 1–2) can pin. Every pure-core task carries complete, compilable code with a failing
test written first. This is the correct spec-vs-plan boundary, not an unfilled TODO.
