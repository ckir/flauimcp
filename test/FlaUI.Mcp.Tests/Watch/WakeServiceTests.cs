using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Watch;
using Xunit;

namespace FlaUI.Mcp.Tests.Watch;

public class WakeServiceTests
{
    // Records registrations + disposals; asserts the null-sink never forwards events anywhere observable.
    private sealed class FakeSource : IUiaEventSource
    {
        public readonly List<WatchSubscriptionSpec> Registered = new();
        public int DisposeCount;
        public IDisposable Register(WatchSubscriptionSpec spec, Action<CapturedEventMeta, object?> onCapture)
        {
            Registered.Add(spec);
            // Fire a bogus event to prove the sink swallows it (no throw, no side effect the service exposes).
            onCapture(new CapturedEventMeta(spec.SubscriptionId, WatchEventKind.StructureChanged, 0, "", DateTime.UtcNow), null);
            return new Disp(this);
        }
        private sealed class Disp : IDisposable
        {
            private readonly FakeSource _s; public Disp(FakeSource s) => _s = s;
            public void Dispose() => _s.DisposeCount++;
        }
    }

    [Fact]
    public async Task Wake_registers_structure_changed_only_and_holds_a_handle()
    {
        var src = new FakeSource();
        var reg = new WakeRegistry();
        var svc = new WakeService(src, reg, windowManager: null); // null WM ok: no WindowInvalidated in this test path
        var wakeId = await svc.WakeAsync("w1", pid: 111);
        Assert.Equal("k1", wakeId);
        var spec = Assert.Single(src.Registered);
        Assert.Equal("w1", spec.WindowId);
        Assert.Equal(new[] { WatchEventKind.StructureChanged }, spec.Kinds);
        Assert.True(reg.TryGet(wakeId, out _));   // held in the registry
        Assert.Equal(0, src.DisposeCount);        // still held
    }

    [Fact]
    public async Task Release_disposes_the_handle_and_is_idempotent()
    {
        var src = new FakeSource();
        var reg = new WakeRegistry();
        var svc = new WakeService(src, reg, windowManager: null);
        var wakeId = await svc.WakeAsync("w1", pid: 111);
        await svc.ReleaseAsync(wakeId);
        Assert.Equal(1, src.DisposeCount);
        Assert.False(reg.TryGet(wakeId, out _));
        await svc.ReleaseAsync(wakeId); // idempotent — no throw, no double dispose
        Assert.Equal(1, src.DisposeCount);
    }

    [Fact]
    public async Task Failed_registration_leaves_no_phantom()
    {
        var src = new ThrowingSource();
        var reg = new WakeRegistry();
        var svc = new WakeService(src, reg, windowManager: null);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.WakeAsync("w1", pid: 111));
        Assert.Empty(reg.List()); // id was reserved then removed on the failed register
    }

    private sealed class ThrowingSource : IUiaEventSource
    {
        public IDisposable Register(WatchSubscriptionSpec spec, Action<CapturedEventMeta, object?> onCapture)
            => throw new InvalidOperationException("boom");
    }
}
