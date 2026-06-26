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
    Timeout
}
