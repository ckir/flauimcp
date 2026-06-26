using FlaUI.Mcp.Core.Errors;
using Xunit;

namespace FlaUI.Mcp.Tests.Errors;

public class ToolExceptionTests
{
    [Fact]
    public void Carries_code_message_and_suggested_recovery()
    {
        var ex = new ToolException(ToolErrorCode.WindowNotFound, "gone", "re-list windows");
        Assert.Equal(ToolErrorCode.WindowNotFound, ex.Code);
        Assert.Equal("gone", ex.Message);
        Assert.Equal("re-list windows", ex.SuggestedRecovery);
    }

    [Fact]
    public void Suggested_recovery_is_optional()
    {
        var ex = new ToolException(ToolErrorCode.Timeout, "waited too long");
        Assert.Null(ex.SuggestedRecovery);
    }

    [Fact]
    public void New_phase3a_codes_serialize_by_name()
    {
        Assert.Equal("WriteBlockedReadOnly", ToolErrorCode.WriteBlockedReadOnly.ToString());
        Assert.Equal("TooManyPendingActions", ToolErrorCode.TooManyPendingActions.ToString());
    }
}
