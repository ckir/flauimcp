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
                    // Absolute System32 path (NOT unqualified "powershell.exe") denies a PATH-hijack
                    // (merge-gate panel, security seat). Windows PowerShell 5.1 is always present here.
                    var psh = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                    var psi = new ProcessStartInfo
                    {
                        FileName = psh,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    psi.ArgumentList.Add("-NoLogo");
                    psi.ArgumentList.Add("-NoProfile");
                    psi.ArgumentList.Add("-NonInteractive");
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add("Add-Type -AssemblyName System.Speech;(New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak($env:FLAUI_TTS_TEXT)");
                    psi.Environment["FLAUI_TTS_TEXT"] = line;
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        // Drain the redirected pipes (else a chatty/erroring child fills the ~4KB OS pipe buffer
                        // and hangs forever) and bound the wait so a hung SAPI child can't leak a zombie
                        // (merge-gate panel, adversarial seat). Runs on the dedicated TTS thread, off the request path.
                        p.OutputDataReceived += static (_, __) => { };
                        p.ErrorDataReceived += static (_, __) => { };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        if (!p.WaitForExit(8000)) { try { p.Kill(entireProcessTree: true); } catch { } }
                    }
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
