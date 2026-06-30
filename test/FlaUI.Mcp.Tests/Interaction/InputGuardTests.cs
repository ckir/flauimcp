using System;
using System.IO;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class InputGuardTests
{
    private const string Sid = "S-1-5-21-1";
    private static DateTime Now => new(2030, 1, 1, 0, 0, 30, DateTimeKind.Utc);

    private static (InputGuard guard, RecordingSyntheticInput sink, FakePlatformEnvironment env) Build(
        InputLease? lease, bool elevated = false, bool allowElevation = false)
    {
        var env = new FakePlatformEnvironment { CanDeliver = true, ForegroundRoot = nint.Zero };
        var sink = new RecordingSyntheticInput(env);   // targets below use Root=nint.Zero, so re-verify matches
        var leaseProv = new StubLeaseProvider(lease, default);
        var guard = new InputGuard(sink, env, leaseProv,
            new ActionBudget(60, 60), new InputAudit(TextWriter.Null),
            currentSid: Sid, isElevated: elevated, allowElevation: allowElevation, clock: () => Now);
        return (guard, sink, env);
    }

    private static InputLease ValidLease(string[]? caps = null) =>
        new(new DateTime(2030, 1, 1, 0, 1, 0, DateTimeKind.Utc), Sid, caps ?? Array.Empty<string>());

    [Fact]
    public void No_lease_refuses_with_InputNotLeased()
    {
        var (g, sink, _) = Build(lease: null);
        var ex = Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
        Assert.Equal(ToolErrorCode.InputNotLeased, ex.Code);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Expired_lease_refuses()
    {
        var expired = new InputLease(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), Sid, Array.Empty<string>());
        var (g, _, _) = Build(expired);
        Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
    }

    [Fact]
    public void Denied_target_refuses_with_TargetDenied()
    {
        var (g, _, _) = Build(ValidLease());
        var ex = Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "consent", "Credential")));
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
    }

    [Fact]
    public void Interlocked_without_shells_cap_refuses_with_SinkInterlocked()
    {
        var (g, _, _) = Build(ValidLease(caps: Array.Empty<string>()));
        var ex = Assert.Throws<ToolException>(() => g.KeyType("ls", new(nint.Zero, 0, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")));
        Assert.Equal(ToolErrorCode.SinkInterlocked, ex.Code);
    }

    [Fact]
    public void Interlocked_with_shells_cap_is_permitted()
    {
        var (g, sink, _) = Build(ValidLease(caps: new[] { "shells" }));
        g.KeyType("ls", new(nint.Zero, 0, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS"));
        Assert.Single(sink.Calls);
    }

    [Fact]
    public void Locked_session_refuses_with_InputDesktopUnavailable()
    {
        var (g, _, env) = Build(ValidLease());
        env.CanDeliver = false;
        var ex = Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
        Assert.Equal(ToolErrorCode.InputDesktopUnavailable, ex.Code);
    }

    [Fact]
    public void Elevated_without_optin_refuses()
    {
        var (g, _, _) = Build(ValidLease(), elevated: true, allowElevation: false);
        var ex = Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
        Assert.Equal(ToolErrorCode.AccessDeniedIntegrity, ex.Code);
    }

    [Fact]
    public void Allowed_target_with_lease_delegates_to_the_sink()
    {
        var (g, sink, _) = Build(ValidLease());
        g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad"));
        Assert.Equal("KeyType:hi", Assert.Single(sink.Calls));
    }

    [Fact]
    public void Drag_denies_when_the_END_endpoint_is_a_denied_target()
    {
        var (g, sink, _) = Build(ValidLease());
        var start = new ActionTarget(nint.Zero, 0, "explorer", "CabinetWClass");
        var end   = new ActionTarget(nint.Zero, 0, "consent", "Credential");   // drop into UAC
        var ex = Assert.Throws<ToolException>(() => g.MouseDrag(0, 0, 10, 10, "left", start, end));
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
        Assert.Empty(sink.Calls);
    }

    private static (InputGuard guard, RecordingSyntheticInput sink, System.IO.StringWriter audit) BuildWithAudit(
        InputLease? lease, DateTime leaseWrite = default, ActionBudget? budget = null)
    {
        var env = new FakePlatformEnvironment { CanDeliver = true, ForegroundRoot = nint.Zero };
        var sink = new RecordingSyntheticInput(env);
        var leaseProv = new StubLeaseProvider(lease, leaseWrite);
        var audit = new System.IO.StringWriter();
        var guard = new InputGuard(sink, env, leaseProv,
            budget ?? new ActionBudget(60, 60), new InputAudit(audit),
            currentSid: Sid, isElevated: false, allowElevation: false, clock: () => Now);
        return (guard, sink, audit);
    }

    [Fact]
    public void Budget_exhaustion_refuses_with_InputBudgetExceeded()
    {
        var (g, sink, _) = BuildWithAudit(ValidLease(), budget: new ActionBudget(maxPerWindow: 2, windowSeconds: 60));
        var t = new ActionTarget(nint.Zero, 0, "notepad", "Notepad");
        g.KeyType("a", t);
        g.KeyType("b", t);
        var ex = Assert.Throws<ToolException>(() => g.KeyType("c", t));
        Assert.Equal(ToolErrorCode.InputBudgetExceeded, ex.Code);
        Assert.Equal(2, sink.Calls.Count); // the 3rd never reached the sink
    }

    [Fact]
    public void A_lease_for_a_different_sid_refuses_with_InputNotLeased()
    {
        var foreign = new InputLease(new DateTime(2030, 1, 1, 0, 1, 0, DateTimeKind.Utc), "S-1-5-21-OTHER", Array.Empty<string>());
        var (g, _, _) = BuildWithAudit(foreign);
        var ex = Assert.Throws<ToolException>(() => g.KeyType("hi", new(nint.Zero, 0, "notepad", "Notepad")));
        Assert.Equal(ToolErrorCode.InputNotLeased, ex.Code);
    }

    [Fact]
    public void Drag_audits_BOTH_endpoints()
    {
        var (g, _, audit) = BuildWithAudit(ValidLease());
        var start = new ActionTarget((nint)11, 100, "explorer", "CabinetWClass"); // start root is NOT re-verified by the sink
        var end   = new ActionTarget(nint.Zero, 200, "notepad", "Notepad");       // end root must match the fake HitTestRoot (0) so the sink re-verify passes
        g.MouseDrag(0, 0, 10, 10, "left", start, end);
        var log = audit.ToString();
        Assert.Contains("window=11", log);          // start endpoint
        Assert.Contains("action=drag-drop", log);   // drop endpoint audited distinctly (F4)
    }

    [Fact]
    public void Budget_exceeded_message_names_a_retry_wait()
    {
        var (g, _, _) = BuildWithAudit(ValidLease(), budget: new ActionBudget(maxPerWindow: 1, windowSeconds: 60));
        var t = new ActionTarget(nint.Zero, 0, "notepad", "Notepad"); // root must match the fake ForegroundRoot (0) so the sink re-verify passes
        g.KeyType("a", t);
        var ex = Assert.Throws<ToolException>(() => g.KeyType("b", t));
        Assert.Contains("Retry in", ex.Message);
    }

    [Fact]
    public void Key_into_a_denied_foreground_root_refuses()
    {
        var (g, sink, _) = BuildWithAudit(ValidLease());
        var ex = Assert.Throws<ToolException>(() => g.KeyChord(System.Array.Empty<string>(), "Enter",
            new ActionTarget(nint.Zero, 0, "consent", "Credential")));
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Type_into_an_embedded_interlocked_element_needs_the_shells_cap()
    {
        // ref-path resolves the ELEMENT's identity (a windowsterminal pane), even if its host window is Allowed
        var (g, sink, _) = BuildWithAudit(ValidLease()); // valid lease, NO shells cap
        var ex = Assert.Throws<ToolException>(() => g.KeyType("dir",
            new ActionTarget((nint)42, 300, "windowsterminal", "CASCADIA_HOSTING_WINDOW_CLASS")));
        Assert.Equal(ToolErrorCode.SinkInterlocked, ex.Code);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Click_at_an_unidentifiable_point_is_denied()
    {
        var (g, sink, _) = BuildWithAudit(ValidLease());
        var ex = Assert.Throws<ToolException>(() => g.MouseClick(5, 5, "left", 1,
            System.Array.Empty<string>(), new ActionTarget((nint)1, 0, null, null))); // no proc, no class
        Assert.Equal(ToolErrorCode.TargetDenied, ex.Code);
        Assert.Empty(sink.Calls);
    }

    private sealed class StubLeaseProvider : ILeaseProvider
    {
        private readonly InputLease? _lease; private readonly DateTime _w;
        public StubLeaseProvider(InputLease? lease, DateTime w) { _lease = lease; _w = w; }
        public InputLease? Read(out DateTime lastWriteUtc) { lastWriteUtc = _w; return _lease; }
    }
}
