// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Fail-closed password decision: if the IsPassword read throws, treat the value as a
/// password (redact), never fall through to reading the text. A false-positive only over-redacts a
/// non-password field — harmless; the safe default. (SHOULD-FIX #2 from Phase 3b-2.)</summary>
public static class RedactionPolicy
{
    public static bool IsPasswordOrFailClosed(System.Func<bool> read)
    {
        try { return read(); } catch { return true; }
    }
}
