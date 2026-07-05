using System;
using System.Collections.Generic;

namespace FlaUI.Mcp.Core.Attention;

/// <summary>Channel-wide (target-agnostic) sliding-window rate limiter for TTS utterances (spec §4.4).
/// Deliberately takes NO target argument so a prompt-injected agent oscillating targets cannot evade it.
/// Thread-safe (locks internally): the signal path may fire from concurrent tool calls.</summary>
public sealed class TtsDebounce
{
    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly Queue<DateTime> _stamps = new();
    private readonly object _lock = new();

    public TtsDebounce(int capacity, TimeSpan window) { _capacity = capacity; _window = window; }

    /// <summary>Take one token at time `now`; true if allowed. Ages out stamps older than the window first.</summary>
    public bool TryTake(DateTime now)
    {
        lock (_lock)
        {
            while (_stamps.Count > 0 && now - _stamps.Peek() >= _window) _stamps.Dequeue();
            if (_stamps.Count >= _capacity) return false;
            _stamps.Enqueue(now);
            return true;
        }
    }
}
