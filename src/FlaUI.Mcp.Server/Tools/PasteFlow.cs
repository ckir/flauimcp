using System;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;

namespace FlaUI.Mcp.Server.Tools;

/// <summary>The side-effects desktop_paste_text needs, injected so the ordering/gating/restore logic
/// in <see cref="PasteFlow"/> is headless-testable with a recording fake (spec §9 safety invariants).
/// The production impl (in InputTools) wraps PerceptionManager / InputGuard / ClipboardAccess.</summary>
public interface IPasteEffects
{
    Task<(ActionTarget target, VerifyRead before)> FocusAndBeforeReadAsync(bool verify);
    void Preflight(ActionTarget target);          // throws ToolException on refusal
    Task<ClipboardSnapshot> SnapshotAsync();
    Task SetClipboardAsync(string text);
    Task PasteAsync(ActionTarget target);         // Ctrl+V; throws on guard refusal
    void AuditForceOverwrite();
    Task<VerifyRead> ReadAfterAsync();            // may throw; PasteFlow treats a throw as read-failed
}

/// <summary>The clipboardRestored wire value + the verify object, produced by the pure flow.</summary>
public readonly record struct PasteOutcome(string ClipboardRestored, VerifyResult Verify);

/// <summary>Pure orchestration of a paste: focus/before-read -> ALL refusal gates -> classify ->
/// (fail-fast non-text) -> set -> Ctrl+V -> confirm-consumption(containment) -> conditional restore +
/// verify. No UIA/clipboard/Win32 here — all effects are injected. Spec §4/§5/§6.</summary>
public static class PasteFlow
{
    private const int VerifySettleMs = 100;

    public static async Task<PasteOutcome> RunAsync(IPasteEffects fx, string text, bool verify,
        bool forceOverwriteClipboard, Func<int, Task> delay)
    {
        var (target, before) = await fx.FocusAndBeforeReadAsync(verify);

        fx.Preflight(target); // ALL refusal gates BEFORE any clipboard mutation (spec §4 step 3)

        var snap = await fx.SnapshotAsync();
        if (snap.Kind == PriorClipboardKind.NonText && !forceOverwriteClipboard)
            throw new ToolException(ToolErrorCode.ClipboardHoldsNonText,
                "The clipboard holds non-text content (image/files) that cannot be preserved.",
                "re-call with forceOverwriteClipboard=true to overwrite it, or clear the clipboard first");

        if (snap.Kind == PriorClipboardKind.NonText) fx.AuditForceOverwrite(); // BEFORE the clobber (Seat 2)
        await fx.SetClipboardAsync(text);   // first mutation
        await fx.PasteAsync(target);        // Ctrl+V through the full guard pipeline

        if (!verify)
            return new PasteOutcome("abandoned", VerifyResult.Disabled);

        await delay(VerifySettleMs);
        VerifyRead after;
        try { after = await fx.ReadAfterAsync(); }
        catch
        {
            return new PasteOutcome("abandoned",
                VerifyResult.From(new VerifyOutcome(VerifyStatus.Skipped, "read-failed", null, null)));
        }

        // Consumption gate = containment (INDEPENDENT of the agent-facing verify outcome; the two answer
        // different questions and MAY disagree, e.g. clipboardRestored:"text" with verify field-not-empty).
        bool consumed = !after.Redacted && after.Text is not null
            && after.Text.Contains(text, StringComparison.Ordinal)
            && !(before.Text?.Contains(text, StringComparison.Ordinal) ?? false);

        string restored = "abandoned";
        if (consumed)
            restored = snap.Kind switch
            {
                PriorClipboardKind.Text => await SetAnd(fx, snap.Text ?? string.Empty, "text"),
                PriorClipboardKind.TextWithRichFormats => await SetAnd(fx, snap.Text ?? string.Empty, "text-degraded"),
                PriorClipboardKind.Empty => await SetAnd(fx, string.Empty, "empty"),
                PriorClipboardKind.NonText => "none-nontext", // forced; cannot restore
                _ => "abandoned",
            };
        else if (snap.Kind == PriorClipboardKind.NonText) restored = "none-nontext";

        VerifyOutcome outcome =
            before.Redacted ? new VerifyOutcome(VerifyStatus.Skipped, "redacted", null, null)
            : before.Text is null ? new VerifyOutcome(VerifyStatus.Skipped, "no-textpattern", null, null)
            : before.Text.Length != 0 ? new VerifyOutcome(VerifyStatus.Skipped, "field-not-empty", null, null)
            : after.Redacted ? new VerifyOutcome(VerifyStatus.Skipped, "redacted", null, null)
            : after.Text is null ? new VerifyOutcome(VerifyStatus.Skipped, "read-failed", null, null)
            : TypedTextVerifier.Check(before.Text, after.Text, text);

        return new PasteOutcome(restored, VerifyResult.From(outcome, after.CanSetValue));
    }

    private static async Task<string> SetAnd(IPasteEffects fx, string text, string label)
    { await fx.SetClipboardAsync(text); return label; }
}
