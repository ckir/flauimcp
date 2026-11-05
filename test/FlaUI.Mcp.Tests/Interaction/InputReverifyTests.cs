// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.Core.Errors;
using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

public class InputReverifyTests
{
    [Fact]
    public void Matching_root_passes()
        => InputReverify.AssertSameRoot(expected: (nint)7, actual: (nint)7); // no throw

    [Fact]
    public void Mismatched_root_aborts_with_ElementDisappeared()
    {
        var ex = Assert.Throws<ToolException>(() => InputReverify.AssertSameRoot((nint)7, (nint)9));
        Assert.Equal(ToolErrorCode.ElementDisappearedDuringAction, ex.Code);
    }
}
