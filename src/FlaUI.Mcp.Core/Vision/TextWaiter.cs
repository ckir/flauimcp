// src/FlaUI.Mcp.Core/Vision/TextWaiter.cs
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FlaUI.Mcp.Core.Vision;

/// <summary>Result of desktop_wait_for_text (§3): satisfied=false on timeout is DATA, not an error.</summary>
public readonly record struct TextWaitResult(bool Satisfied);

/// <summary>Pure poll loop for desktop_wait_for_text (§3/§9). Repeatedly runs a probe (capture+OCR+match, injected)
/// until it returns true or the budget elapses, enforcing a HARD minimum interval between passes (§3 Seat C:
/// Windows.Media.Ocr is heavy — never tight-loop). The probe itself must run OFF the query STA (the caller wires
/// ScreenCapture + TextFinder, both STA-free). Timeout -> Satisfied=false.</summary>
public static class TextWaiter
{
    public const int MinPollIntervalMs = 750; // §3 Seat C hard floor for the live tool

    public static async Task<TextWaitResult> WaitAsync(Func<Task<bool>> probe, int timeoutMs, int minIntervalMs)
    {
        int interval = Math.Max(1, minIntervalMs);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (await probe()) return new TextWaitResult(true);
            if (sw.ElapsedMilliseconds >= timeoutMs) return new TextWaitResult(false);
            // Sleep the throttle, but don't overshoot the remaining budget by much.
            int remaining = (int)(timeoutMs - sw.ElapsedMilliseconds);
            if (remaining <= 0) return new TextWaitResult(false);
            await Task.Delay(Math.Min(interval, remaining));
            if (sw.ElapsedMilliseconds >= timeoutMs) return new TextWaitResult(false);
        }
    }
}
