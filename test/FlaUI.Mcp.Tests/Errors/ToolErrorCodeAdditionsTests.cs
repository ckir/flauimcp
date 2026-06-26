using FlaUI.Mcp.Core.Errors;
using Xunit;

namespace FlaUI.Mcp.Tests.Errors;

public class ToolErrorCodeAdditionsTests
{
    [Theory]
    [InlineData("InvalidArguments")]
    [InlineData("CaptureUnavailable")]
    [InlineData("SnapshotNotFound")]
    [InlineData("SnapshotWindowMismatch")]
    [InlineData("SelectorNoMatch")]
    [InlineData("NoFocusedElement")]
    [InlineData("NotImplemented")]
    public void New_codes_are_defined(string name)
        => Assert.True(Enum.IsDefined(typeof(ToolErrorCode), Enum.Parse<ToolErrorCode>(name)));
}
