// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

namespace FlaUI.Mcp.Core.Errors;

public enum ToolErrorCode
{
    WindowNotFound,
    WindowHandleStale,
    RefNotFound,
    RefStaleUnresolvable,
    PatternUnsupported,
    ElementNotActionable,
    AmbiguousMatch,
    LaunchTimeout,
    AccessDeniedIntegrity,
    ActionBlockedPending,
    ElementDisappearedDuringAction,
    UacPromptDetected,
    TargetDenied,
    WriteBlockedReadOnly,
    TooManyPendingActions,
    Timeout,
    InvalidArguments,
    CaptureUnavailable,
    SnapshotNotFound,
    SnapshotWindowMismatch,
    SelectorNoMatch,
    NoFocusedElement,
    NotImplemented,
    ClipboardUnavailable,
    GridCellOutOfRange,
    InputNotLeased,
    InputDesktopUnavailable,
    InputBudgetExceeded,
    SinkInterlocked,
    ClipboardHoldsNonText,
    TooManyWatches,
    OcrUnavailable,
    RedactionUnmaskable
}
