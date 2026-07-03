// src/FlaUI.Mcp.Core/Watch/Uia3EventSource.cs
using System;
using System.Collections.Generic;
using System.Threading;
using FlaUI.Core;                 // CacheRequest
using FlaUI.Core.AutomationElements; // AutomationElement, Window
using FlaUI.Core.Definitions;     // TreeScope, AutomationElementMode
using FlaUI.Core.EventHandlers;   // AutomationEventHandlerBase, StructureChangedEventHandlerBase, FocusChangedEventHandlerBase
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Core.Watch;

/// <summary>The FlaUI/UIA3 implementation of <see cref="IUiaEventSource"/>. All UIA (un)registration runs on
/// the WindowManager query STA (UIA is apartment-affine). Global kinds (window_opened / window_closed /
/// focus_changed) are process-wide singletons ref-counted across subscriptions (§7); structure_changed is
/// per-subscription. Event callbacks fire on COM RPC threads and read CACHED props only, never throwing
/// (§6/§16.4). BUILD-ONLY here — Desktop-tested in Task 10.</summary>
public sealed class Uia3EventSource : IUiaEventSource
{
    private readonly WindowManager _windowManager;
    private readonly object _gate = new();

    /// <summary>Per-kind shared registration for the process-wide global event kinds (§7). The first
    /// subscriber for a kind registers the single UIA handler; the last to leave unregisters it. Guarded
    /// by <see cref="_gate"/> against the COM fan-out reading it concurrently.</summary>
    private sealed class GlobalKindReg
    {
        public int RefCount;
        public object? Token;               // AutomationEventHandlerBase (window_*) or FocusChangedEventHandlerBase (focus)
        public AutomationElement? Element;   // desktop root the window_* handler was registered on (for unregister)
        public AutomationBase? Automation;   // the automation the focus handler was registered on (for unregister)
        public readonly List<Subscriber> Subscribers = new();
    }

    // Reference-identity holder (NOT a record) so list Add/Remove key on the exact instance, never on
    // structural equality of two subscriptions that happen to share a spec/delegate.
    private sealed class Subscriber
    {
        public Subscriber(WatchSubscriptionSpec spec, Action<CapturedEventMeta, object?> onCapture)
        { Spec = spec; OnCapture = onCapture; }
        public WatchSubscriptionSpec Spec { get; }
        public Action<CapturedEventMeta, object?> OnCapture { get; }
    }

    private readonly Dictionary<WatchEventKind, GlobalKindReg> _globals = new();

    public Uia3EventSource(WindowManager windowManager) => _windowManager = windowManager;

    /// <inheritdoc />
    public IDisposable Register(WatchSubscriptionSpec spec, Action<CapturedEventMeta, object?> onCapture)
    {
        var subscriber = new Subscriber(spec, onCapture);
        // Do ALL registration on the query STA. The seam is synchronous, so block on the STA task.
        return _windowManager.RunWithWindowAndDesktopAsync(
            new WindowHandle(spec.WindowId),
            (win, desktop) => RegisterOnSta(win, desktop, spec, subscriber))
            .GetAwaiter().GetResult();
    }

    // Runs on the query STA. Builds the ProcessId+RuntimeId-only cache request, registers each requested
    // kind under a TIGHT using(cr.Activate()) scope, and is transactional: any failure unwinds every
    // handler already added before rethrowing (§16.2) so no live handler is ever orphaned.
    private IDisposable RegisterOnSta(Window win, AutomationElement desktop, WatchSubscriptionSpec spec, Subscriber subscriber)
    {
        var automation = desktop.Automation;

        // Cache ONLY ProcessId + RuntimeId (§16.1). PER SPIKE A: AutomationElementMode.None + TreeScope.Element,
        // Add(automation.PropertyLibrary.Element.ProcessId) + Add(...RuntimeId).
        var cr = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.None,
            TreeScope = TreeScope.Element,
        };
        cr.Add(automation.PropertyLibrary.Element.ProcessId);
        cr.Add(automation.PropertyLibrary.Element.RuntimeId);

        var joinedGlobals = new List<WatchEventKind>();
        StructureChangedEventHandlerBase? structureHandler = null;

        try
        {
            foreach (var kind in spec.Kinds)
            {
                switch (kind)
                {
                    case WatchEventKind.StructureChanged:
                        // PER SPIKE A: win.RegisterStructureChangedEvent(TreeScope.Subtree,
                        //   Action<AutomationElement, StructureChangeType, int[]>) -> StructureChangedEventHandlerBase.
                        // Registered on the window root; scope narrowing to ScopeRef is refined in Task 8.
                        using (cr.Activate()) // PER SPIKE A: CacheRequest.Activate() is thread-local; scope it TIGHT around the register call, pop immediately.
                        {
                            structureHandler = win.RegisterStructureChangedEvent(
                                TreeScope.Subtree,
                                (element, changeType, runtimeId) => Handle(subscriber, element, WatchEventKind.StructureChanged));
                        }
                        break;

                    case WatchEventKind.WindowOpened:
                    case WatchEventKind.WindowClosed:
                    case WatchEventKind.FocusChanged:
                        JoinGlobal(kind, subscriber, desktop, automation, cr);
                        joinedGlobals.Add(kind);
                        break;
                }
            }
        }
        catch
        {
            // Transactional rollback (§16.2): already on the query STA, so unregister directly (no self-marshal).
            if (structureHandler is not null)
                try { win.FrameworkAutomationElement.UnregisterStructureChangedEventHandler(structureHandler); } catch { }
            foreach (var kind in joinedGlobals)
                try { LeaveGlobal(kind, subscriber); } catch { }
            throw;
        }

        return new Subscription(this, win, subscriber, structureHandler, joinedGlobals);
    }

    // Registers (or shares) the single process-wide UIA handler for a global kind (§7). The FIRST subscriber
    // actually registers under cr.Activate(); later ones just join the fan-out list and bump the ref-count.
    private void JoinGlobal(WatchEventKind kind, Subscriber subscriber, AutomationElement desktop, AutomationBase automation, CacheRequest cr)
    {
        lock (_gate)
        {
            if (!_globals.TryGetValue(kind, out var reg))
            {
                reg = new GlobalKindReg();
                _globals[kind] = reg;
            }
            if (reg.RefCount == 0)
            {
                using (cr.Activate()) // PER SPIKE A: tight Activate scope around the register call on the shared query STA.
                {
                    switch (kind)
                    {
                        case WatchEventKind.WindowOpened:
                            // PER SPIKE A: desktop.RegisterAutomationEvent(automation.EventLibrary.Window.WindowOpenedEvent,
                            //   TreeScope.Descendants, Action<AutomationElement, EventId>) -> AutomationEventHandlerBase. GLOBAL on the Desktop root.
                            reg.Token = desktop.RegisterAutomationEvent(
                                automation.EventLibrary.Window.WindowOpenedEvent, TreeScope.Descendants,
                                (element, evtId) => FanOut(WatchEventKind.WindowOpened, element));
                            reg.Element = desktop;
                            break;
                        case WatchEventKind.WindowClosed:
                            // PER SPIKE A: desktop.RegisterAutomationEvent(automation.EventLibrary.Window.WindowClosedEvent,
                            //   TreeScope.Descendants, Action<AutomationElement, EventId>). Spike A caveat: UIA WindowClosed may
                            //   not deliver — register anyway; the RELIABLE close path is the synthetic one in Task 8.
                            reg.Token = desktop.RegisterAutomationEvent(
                                automation.EventLibrary.Window.WindowClosedEvent, TreeScope.Descendants,
                                (element, evtId) => FanOut(WatchEventKind.WindowClosed, element));
                            reg.Element = desktop;
                            break;
                        case WatchEventKind.FocusChanged:
                            // PER SPIKE A: automation.RegisterFocusChangedEvent(Action<AutomationElement>) -> FocusChangedEventHandlerBase.
                            //   GLOBAL on the automation (NOT on an element).
                            reg.Token = automation.RegisterFocusChangedEvent(
                                element => FanOut(WatchEventKind.FocusChanged, element));
                            reg.Automation = automation;
                            break;
                    }
                }
            }
            reg.Subscribers.Add(subscriber);
            reg.RefCount++;
        }
    }

    // Removes a subscriber from a global kind and, when the LAST one leaves, unregisters the shared UIA handler.
    // Idempotent: a subscriber not present is a no-op. MUST be called on the query STA (UIA unregister is apartment-affine).
    private void LeaveGlobal(WatchEventKind kind, Subscriber subscriber)
    {
        lock (_gate)
        {
            if (!_globals.TryGetValue(kind, out var reg)) return;
            if (!reg.Subscribers.Remove(subscriber)) return; // idempotent — not present (double dispose / already torn down)
            reg.RefCount--;
            if (reg.RefCount <= 0)
            {
                try
                {
                    switch (kind)
                    {
                        case WatchEventKind.WindowOpened:
                        case WatchEventKind.WindowClosed:
                            // [assumed by Spike A (XML-doc only): element.UnregisterAutomationEventHandler(handler)]
                            //   -> [actual, reflection-verified: element.FrameworkAutomationElement.UnregisterAutomationEventHandler(handler)]
                            //   because in FlaUI 5.0.0 AutomationElement implements IAutomationElementEventSubscriber (register) but the
                            //   unregister methods live on FrameworkAutomationElementBase (IAutomationElementEventUnsubscriber). PER SPIKE A otherwise.
                            if (reg.Token is AutomationEventHandlerBase h && reg.Element is not null)
                                reg.Element.FrameworkAutomationElement.UnregisterAutomationEventHandler(h);
                            break;
                        case WatchEventKind.FocusChanged:
                            // PER SPIKE A (exact match): automation.UnregisterFocusChangedEvent(FocusChangedEventHandlerBase handler).
                            if (reg.Token is FocusChangedEventHandlerBase fh && reg.Automation is not null)
                                reg.Automation.UnregisterFocusChangedEvent(fh);
                            break;
                    }
                }
                catch { /* the handler may already be dead (window closed) — swallow */ }
                _globals.Remove(kind);
            }
        }
    }

    // Fan-out for a shared global handler: snapshot the current subscribers under the lock, then deliver
    // outside it. Runs on a COM RPC thread — never throws.
    private void FanOut(WatchEventKind kind, AutomationElement element)
    {
        try
        {
            Subscriber[] snapshot;
            lock (_gate)
            {
                if (!_globals.TryGetValue(kind, out var reg)) return;
                snapshot = reg.Subscribers.ToArray();
            }
            foreach (var s in snapshot)
                Handle(s, element, kind);
        }
        catch { /* never throw on a COM callback thread (mirrors Handle's catch-all) */ }
    }

    // Runs on the COM callback thread. Reads ONLY cached props (no live round-trip) and MUST NOT throw:
    // an unhandled throw on a COM callback thread can crash the process (§16.4).
    private static void Handle(Subscriber subscriber, AutomationElement element, WatchEventKind kind)
    {
        try
        {
            int pid;
            string ridStr;
            try
            {
                // PER SPIKE A: element.Properties.ProcessId.ValueOrDefault / element.Properties.RuntimeId.ValueOrDefault
                //   read the cache populated at registration — no live cross-apartment fetch. RuntimeId is int[].
                pid = element.Properties.ProcessId.ValueOrDefault;
                var rid = element.Properties.RuntimeId.ValueOrDefault;
                ridStr = rid is null ? "" : string.Join(",", rid);
            }
            catch
            {
                // Undeliverable cached read (§16.4): treat as pid=0 / rid="".
                pid = 0;
                ridStr = "";
            }

            var meta = new CapturedEventMeta(subscriber.Spec.SubscriptionId, kind, pid, ridStr, DateTime.UtcNow);
            // Source token = the live FlaUI element for the later STA payload-build; null for window_closed (source is dead/absent).
            subscriber.OnCapture(meta, kind == WatchEventKind.WindowClosed ? null : element);
        }
        catch
        {
            // Never throw on a COM callback thread.
        }
    }

    // The per-subscription disposable. Disposing from ANY thread self-marshals teardown onto the query STA
    // (UIA is apartment-affine — a Dispose from an MTA/ThreadPool thread throws RPC_E_WRONG_THREAD). Idempotent.
    private sealed class Subscription : IDisposable
    {
        private readonly Uia3EventSource _source;
        private readonly Window _win;
        private readonly Subscriber _subscriber;
        private readonly StructureChangedEventHandlerBase? _structureHandler;
        private readonly IReadOnlyList<WatchEventKind> _joinedGlobals;
        private int _disposed;

        public Subscription(
            Uia3EventSource source, Window win, Subscriber subscriber,
            StructureChangedEventHandlerBase? structureHandler, IReadOnlyList<WatchEventKind> joinedGlobals)
        {
            _source = source;
            _win = win;
            _subscriber = subscriber;
            _structureHandler = structureHandler;
            _joinedGlobals = joinedGlobals;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent — dispose twice is safe
            try
            {
                // Self-marshal onto the query STA (mirrors WindowManager.PostToQuerySta reasoning), blocking
                // since Dispose is synchronous. Unregister on the SAME STA the handlers were registered on.
                _source._windowManager.RunOnQueryAsync(() => { TearDownOnSta(); return true; })
                    .GetAwaiter().GetResult();
            }
            catch
            {
                // Teardown is best-effort; handlers may already be dead (window/process gone).
            }
        }

        private void TearDownOnSta()
        {
            if (_structureHandler is not null)
                try { _win.FrameworkAutomationElement.UnregisterStructureChangedEventHandler(_structureHandler); } catch { }
            foreach (var kind in _joinedGlobals)
                try { _source.LeaveGlobal(kind, _subscriber); } catch { }
        }
    }
}
