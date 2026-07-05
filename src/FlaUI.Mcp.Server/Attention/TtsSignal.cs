using System;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using FlaUI.Mcp.Core.Attention;
using FlaUI.Mcp.Core.Windows;

namespace FlaUI.Mcp.Server.Attention;

/// <summary>Opt-in spoken attention channel (spec §4.4). Only constructed when `--autosound` is on, so
/// Enabled is always true here. Debounced channel-wide (target-agnostic) via TtsDebounce. Speaks ONLY the
/// target app's name — never the cross-process foreground title (leak rule §4.1). Best-effort; never throws.</summary>
public sealed class TtsSignal : IAttentionSignal
{
    private readonly Func<WindowHandle, string?> _appNameOf;   // maps target handle → its own app name (already known)
    private readonly TtsDebounce _debounce;
    private readonly Func<DateTime> _clock;

    public TtsSignal(Func<WindowHandle, string?> appNameOf, TtsDebounce debounce, Func<DateTime>? clock = null)
    { _appNameOf = appNameOf; _debounce = debounce; _clock = clock ?? (() => DateTime.UtcNow); }

    public bool Enabled => true;

    public static string Utterance(string? appName) =>
        string.IsNullOrWhiteSpace(appName) ? "Please switch to the window the assistant is waiting on."
                                           : $"Please click {appName}.";

    public void Signal(WindowHandle target)
    {
        try
        {
            if (!_debounce.TryTake(_clock())) return;   // channel-wide rate cap
            var line = Utterance(_appNameOf(target));
            _ = Task.Run(() =>
            {
                try { using var s = new SpeechSynthesizer(); s.Speak(line); }
                catch { /* no audio device / synth failure → silent */ }
            });
        }
        catch { /* never throw from the signal path */ }
    }
}
