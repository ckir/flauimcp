// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

namespace FlaUI.Mcp.Tests.Perception;

internal static class RefLineHelper
{
    /// <summary>From a fullProperties snapshot tree, return the e-ref on the line carrying aid=&lt;automationId&gt;.</summary>
    public static string RefFor(string tree, string automationId)
    {
        foreach (var line in tree.Split('\n'))
            if (line.Contains("aid=" + automationId))
            {
                int lb = line.IndexOf('['), rb = line.IndexOf(']');
                return line.Substring(lb + 1, rb - lb - 1);
            }
        throw new Xunit.Sdk.XunitException($"no ref line for aid={automationId} in:\n{tree}");
    }
}
