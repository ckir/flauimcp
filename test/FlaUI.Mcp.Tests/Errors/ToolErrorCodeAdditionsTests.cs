// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

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
