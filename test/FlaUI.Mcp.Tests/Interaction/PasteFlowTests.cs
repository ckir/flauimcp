using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using FlaUI.Mcp.Server.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class PasteFlowTests
{
    // Recording fake: logs the ORDER of side-effects and lets each test script the reads/snapshot.
    sealed class FakeEffects : IPasteEffects
    {
        public List<string> Log = new();
        public ActionTarget Target = new(1, 0, "notepad", "Notepad");
        public VerifyRead Before, After;
        public bool AfterThrows;
        public ClipboardSnapshot Snap = new(PriorClipboardKind.Empty, null);
        public Exception? PreflightThrows, PasteThrows;

        public Task<(ActionTarget, VerifyRead)> FocusAndBeforeReadAsync(bool verify)
        { Log.Add("focus"); return Task.FromResult((Target, Before)); }
        public void Preflight(ActionTarget t) { Log.Add("preflight"); if (PreflightThrows is { } e) throw e; }
        public Task<ClipboardSnapshot> SnapshotAsync() { Log.Add("snapshot"); return Task.FromResult(Snap); }
        public Task SetClipboardAsync(string text) { Log.Add("set:" + text); return Task.CompletedTask; }
        public Task PasteAsync(ActionTarget t) { Log.Add("paste"); if (PasteThrows is { } e) throw e; return Task.CompletedTask; }
        public void AuditForceOverwrite() => Log.Add("audit-force");
        public Task<VerifyRead> ReadAfterAsync() { Log.Add("read-after"); if (AfterThrows) throw new Exception("uia"); return Task.FromResult(After); }
    }

    static Task<PasteOutcome> Run(FakeEffects fx, string text = "bar", bool verify = true, bool force = false)
        => PasteFlow.RunAsync(fx, text, verify, force, _ => Task.CompletedTask); // delay is a no-op in tests

    [Fact]
    public async Task Refused_preflight_never_touches_the_clipboard()
    {
        var fx = new FakeEffects { PreflightThrows = new ToolException(ToolErrorCode.InputNotLeased, "x", "y") };
        await Assert.ThrowsAsync<ToolException>(() => Run(fx));
        Assert.DoesNotContain(fx.Log, s => s.StartsWith("set:"));   // clipboard NEVER mutated
        Assert.DoesNotContain("snapshot", fx.Log);                  // refused before we even classify
    }

    [Fact]
    public async Task NonText_without_force_throws_before_any_set()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.NonText, null) };
        var ex = await Assert.ThrowsAsync<ToolException>(() => Run(fx, force: false));
        Assert.Equal(ToolErrorCode.ClipboardHoldsNonText, ex.Code);
        Assert.DoesNotContain(fx.Log, s => s.StartsWith("set:"));
    }

    [Fact]
    public async Task NonText_with_force_audits_BEFORE_it_overwrites()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.NonText, null) };
        await Run(fx, force: true);
        Assert.True(fx.Log.IndexOf("audit-force") < fx.Log.IndexOf("set:bar")); // audit precedes clobber (Seat 2)
    }

    [Fact]
    public async Task Preflight_runs_before_the_first_clipboard_set()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Empty, null),
            Before = new VerifyRead { Text = "" }, After = new VerifyRead { Text = "bar" } };
        await Run(fx);
        Assert.True(fx.Log.IndexOf("preflight") < fx.Log.IndexOf("set:bar"));
    }

    [Fact]
    public async Task Verify_false_never_restores_and_reports_abandoned()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR") };
        var r = await Run(fx, verify: false);
        Assert.Equal("abandoned", r.ClipboardRestored);
        Assert.DoesNotContain("read-after", fx.Log);
        Assert.DoesNotContain(fx.Log, s => s == "set:PRIOR");       // prior NOT restored
    }

    [Fact]
    public async Task Confirmed_containment_restores_prior_text()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR"),
            Before = new VerifyRead { Text = "foo" }, After = new VerifyRead { Text = "foobar" } };
        var r = await Run(fx);                                       // after contains "bar", before did not
        Assert.Equal("text", r.ClipboardRestored);
        Assert.Contains("set:PRIOR", fx.Log);
    }

    [Fact]
    public async Task Unconfirmed_containment_abandons_and_leaves_payload()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR"),
            Before = new VerifyRead { Text = "foo" }, After = new VerifyRead { Text = "foo" } }; // paste didn't land
        var r = await Run(fx);
        Assert.Equal("abandoned", r.ClipboardRestored);
    }

    [Fact]
    public async Task Rich_clipboard_confirmed_reports_text_degraded()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.TextWithRichFormats, "PRIOR"),
            Before = new VerifyRead { Text = "" }, After = new VerifyRead { Text = "bar" } };
        var r = await Run(fx);
        Assert.Equal("text-degraded", r.ClipboardRestored);
        Assert.Contains("set:PRIOR", fx.Log);            // restored the SAVED prior plain text, not the payload
    }

    [Fact]
    public async Task After_read_failure_is_soft_and_abandons()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR"),
            Before = new VerifyRead { Text = "" }, AfterThrows = true };
        var r = await Run(fx);
        Assert.Equal("abandoned", r.ClipboardRestored);
        Assert.Equal("read-failed", r.Verify.Reason);
    }

    [Fact]
    public async Task Keystroke_fault_propagates_and_never_restores()   // Seat 2: paste throws -> abort, no restore
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR"),
            Before = new VerifyRead { Text = "" }, PasteThrows = new ToolException(ToolErrorCode.InputBudgetExceeded, "x", "y") };
        await Assert.ThrowsAsync<ToolException>(() => Run(fx));
        Assert.DoesNotContain("read-after", fx.Log);                    // never reached confirmation
        Assert.DoesNotContain(fx.Log, s => s == "set:PRIOR");           // prior clipboard NOT restored
    }

    [Fact]
    public async Task Empty_prior_clipboard_confirmed_reports_empty()   // Seat 2
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Empty, null),
            Before = new VerifyRead { Text = "" }, After = new VerifyRead { Text = "bar" } };
        var r = await Run(fx);
        Assert.Equal("empty", r.ClipboardRestored);
        Assert.Contains(fx.Log, s => s == "set:");        // restored an EMPTY clipboard, not stale prior text
    }

    [Fact]
    public async Task Forced_nontext_reports_none_nontext()             // Seat 2
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.NonText, null),
            Before = new VerifyRead { Text = "" }, After = new VerifyRead { Text = "bar" } };
        var r = await Run(fx, force: true);
        Assert.Equal("none-nontext", r.ClipboardRestored);
        Assert.Equal(1, fx.Log.Count(s => s.StartsWith("set:")));  // ONLY the payload set; no restore write
    }

    [Fact]
    public async Task Redacted_after_read_blocks_restore_and_reports_redacted()
    {
        var fx = new FakeEffects { Snap = new(PriorClipboardKind.Text, "PRIOR"),
            Before = new VerifyRead { Text = "" }, After = new VerifyRead { Text = "secret", Redacted = true } };
        var r = await Run(fx);
        Assert.Equal("abandoned", r.ClipboardRestored);          // redacted after-read cannot confirm containment
        Assert.DoesNotContain(fx.Log, s => s == "set:PRIOR");    // prior NOT restored off a redacted read
        Assert.Equal("redacted", r.Verify.Reason);               // verify skips as redacted
    }
}
