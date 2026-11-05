// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

namespace FlaUI.Mcp.Core.Errors;

/// <summary>An agent-recoverable error. Carries a stable code and an optional
/// concrete next move for the agent loop.</summary>
public sealed class ToolException : Exception
{
    public ToolErrorCode Code { get; }
    public string? SuggestedRecovery { get; }

    public ToolException(ToolErrorCode code, string message, string? suggestedRecovery = null)
        : base(message)
    {
        Code = code;
        SuggestedRecovery = suggestedRecovery;
    }
}
