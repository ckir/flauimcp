# Spike B findings — `McpServer` notification over stdio + host-surfacing + DELIVERY DECISION

**Status:** ✅ GATE CLEARED. Resolves plan §14.2 + §15a. Throwaway `_SpikeBTool.cs` built + deployed + invoked
live, then removed. Package: **ModelContextProtocol / ModelContextProtocol.Core 1.4.0**, `lib/net10.0`.

## DELIVERY DECISION: **push+drain** (user-approved 2026-07-03; agy-first, cascade `f0977c1d` recommended B; my read matched)

Claude Code (our primary consumer) does NOT surface unsolicited server→client notifications to the model (measured
below). So a **drain/poll path is required**. We keep the push emit too (cheap, MCP-native, MCP-Inspector-friendly)
→ Tasks 8–10 build BOTH: the §6 push pipeline (`McpEventSink` over `McpServer.SendNotificationAsync`) AND a bounded
per-subscription buffer + `desktop_drain_events(subscriptionId, max?)` that Claude Code polls.

## §14.2 — how a tool obtains the server + sends a notification (COMPILER-CONFIRMED)

**⚠️ PLAN DIVERGENCE `[assumed IMcpServer] → [actual McpServer]`.** ModelContextProtocol 1.4.0 exposes **no
`IMcpServer` interface** (not in the XML doc, not a type in the assembly). The injectable type is the concrete
class **`ModelContextProtocol.Server.McpServer`**. Tasks 8/9 MUST use `McpServer`, not `IMcpServer`.

```csharp
using ModelContextProtocol.Server;   // McpServer, McpServerTool
// A [McpServerTool] method receives McpServer by DI injection as a plain parameter:
public static async Task<string> Emit(McpServer server, CancellationToken ct)
{
    var payload = new { subscriptionId="s0", @event="window_opened", window="w0",
                        coalescedCount=1, timestampUtc="2026-07-03T10:00:00.000Z" };
    await server.SendNotificationAsync("notifications/flaui/desktop_event", payload, cancellationToken: ct);
    return "...";
}
```

- `SendNotificationAsync` is an instance method on `McpSession` (McpServer's base). Overloads:
  `SendNotificationAsync(string method, CancellationToken)` and the generic
  `SendNotificationAsync<T>(string method, T notificationParams, JsonSerializerOptions? = null, CancellationToken = default)`.
- Serialization = **STJ** (System.Text.Json). The `DesktopEventPayload` record's explicit `[JsonPropertyName]`s
  (Task 3) make the wire keys deterministic — no serializer-options plumbing needed for the emit.
- The hosted-service pump (Task 9) can obtain `McpServer` via DI after host start, or stash it from a first tool
  call; a `[McpServerTool]`-method parameter injection is confirmed working.

## §15a — host-surfacing check (the load-bearing half): Claude Code does NOT surface push

Deployed the probe build (published single-file self-contained → copied over the install-path exe), user `/mcp`
reconnected (tool `debug_emit_test_event` appeared), and **Claude (as the MCP client) invoked it**. Result:

```
tool return: {"ok":true,"emitted":"notifications/flaui/desktop_event"}
```

**Only the tool return arrived. The `notifications/flaui/desktop_event` push did NOT appear as a separate event in
the model's conversation context.** The emit itself succeeded (no throw). So push works on the wire but is not
surfaced to the model by this host → **drain is mandatory for Claude Code.** (Not re-tested against MCP Inspector;
the push path is retained partly so that standard client can be used to watch events during Task 10 development.)

## Risks to FOLD into Task 8/9/10 (from the agy consult, verified)

1. **Ref-eviction desync (biggest).** A drained payload's `ref` is minted into the bounded event-ref layer
   (`RefRegistry.EventRefCap = 64`/window, Task 7a). If ≥64 newer events mint refs before the agent drains, a
   buffered payload's `ref` is already evicted → `REF_NOT_FOUND` on use. This is **throughput/time-bound, not just a
   capacity relationship** — so keep the drain buffer SMALL (≤ ~20) AND, decisively, **document that a stale drained
   `ref` requires a fresh `desktop_snapshot`** (the doc note is the real backstop; a small buffer only reduces the
   window). Do NOT add a TTL that expires a buffered ref before the poll.
2. **Double-loss accounting.** The upstream `EventCoalescer` reports `droppedCount`; a bounded downstream drain ring
   is a SECOND loss point. `desktop_drain_events` (and `desktop_list_watches`' `droppedCount`) must report the
   **sum** of coalescer drops + drain-buffer evictions so the agent sees true missed-state.
3. **Double-delivery (future clients).** If a future host DOES surface push AND the agent also polls
   `desktop_drain_events`, it sees each event twice. Mitigation: the `desktop_drain_events` tool description must
   say it is for hosts that do not surface push (e.g. Claude Code today); a push-surfacing client should not poll.

## What Tasks 8–10 can now write

- `McpEventSink : IEventSink` (Server) = `McpServer.SendNotificationAsync("notifications/flaui/desktop_event", payload, ct)`.
- ALSO the drain path: `WatchService` owns a bounded per-subscription buffer; `WatchPump` appends the built payload
  to it (in addition to `sink.EmitAsync`); pinned event refs survive until drained (no TTL). `WatchTools` gains
  `desktop_drain_events(subscriptionId, max?)` returning+clearing the buffer, reporting summed dropped counts.
- Use `McpServer` (concrete), not `IMcpServer`, everywhere the plan said `IMcpServer`.
