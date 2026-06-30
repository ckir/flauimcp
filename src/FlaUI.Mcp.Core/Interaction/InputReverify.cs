using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>The atomic pre-send re-verify DECISION (pure). The real 4b leaf (and the 4a recording fake)
/// call this with the live foreground/point root read just before the send; a mismatch means a
/// focus-steal / overlay moved in the gap → abort, fire nothing.</summary>
public static class InputReverify
{
    public static void AssertSameRoot(nint expected, nint actual)
    {
        if (expected != actual)
            throw new ToolException(ToolErrorCode.ElementDisappearedDuringAction,
                "The target's window lost focus / changed under the pointer just before sending.",
                "re-snapshot, re-focus the target, and retry");
    }
}
