// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

namespace FlaUI.Mcp.Core.Tray;

/// <summary>CloseNow: invoke the chevron now. Restored: the `overflowRestored` field (null = absent on the wire).</summary>
public readonly record struct OverflowPlan(bool CloseNow, bool? Restored);

/// <summary>SPEC §1.2 / §1.3 "Overflow after an action", pure. The tool closes the overflow only if IT
/// opened it; it never re-opens one that an action closed; it reports `overflowRestored` whenever it
/// opened or found the overflow, true iff the end state equals the start state.</summary>
public static class OverflowRestore
{
    public static OverflowPlan Decide(bool wasOpenAtStart, bool openedByTool, bool isOpenNow)
    {
        bool closeNow = openedByTool && isOpenNow;
        bool endOpen = isOpenNow && !closeNow;
        bool? restored = (openedByTool || wasOpenAtStart) ? endOpen == wasOpenAtStart : null;
        return new OverflowPlan(closeNow, restored);
    }

    /// <summary>The `overflowRestored` value from the REAL end state, measured after the close attempt
    /// (Decide reports the planned end state). null = the tool never opened nor found the overflow open.</summary>
    public static bool? Report(bool wasOpenAtStart, bool openedByTool, bool isOpenAfter)
        => (openedByTool || wasOpenAtStart) ? isOpenAfter == wasOpenAtStart : null;
}
