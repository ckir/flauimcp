using System;
using System.Diagnostics;
using System.Threading;
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
            // Run the fire-and-forget child-process spawn on a dedicated background thread so the attention
            // path never blocks on process creation. (The STA apartment below is vestigial from the old
            // in-process System.Speech approach — harmless for a process spawn.) Best-effort — never throws.
            var t = new Thread(() =>
            {
                try
                {
                    // System.Speech's voice-engine COM resolution NREs in THIS MCP-server process context — proven
                    // live: identical code speaks fine in a fresh pwsh .NET 10 process (STA and MTA threads, default
                    // and explicit voice), but throws NullReferenceException in GetVoice/GetComEngine here. So do the
                    // speech in a short-lived CHILD process that gets clean COM state. The utterance is passed via an
                    // environment variable (no command injection), and all std streams are redirected so the child
                    // never inherits — or corrupts — this server's JSON-RPC stdio.
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",   // Windows PowerShell 5.1 (always present); speaks reliably
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    psi.ArgumentList.Add("-NoProfile");
                    psi.ArgumentList.Add("-NonInteractive");
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add("Add-Type -AssemblyName System.Speech;(New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak($env:FLAUI_TTS_TEXT)");
                    psi.Environment["FLAUI_TTS_TEXT"] = line;
                    var p = Process.Start(psi);
                    if (p != null) { p.EnableRaisingEvents = true; p.Exited += (_, __) => { try { p.Dispose(); } catch { } }; }
                }
                catch (Exception ex)
                {
                    // Best-effort, but log to STDERR (the server's log channel — never stdout/JSON-RPC) so a future
                    // failure isn't invisible like the original swallowed catch that hid the System.Speech NRE.
                    try { Console.Error.WriteLine("[flaui-mcp] autosound TTS spawn failed: " + ex.Message); } catch { }
                }
            })
            { IsBackground = true, Name = "flaui-mcp-tts" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
        catch { /* never throw from the signal path */ }
    }
}
