// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

namespace FlaUI.Mcp.Server.Logging;

/// <summary>Per-call logging scope, opened by ToolCallLogFilter. `Sensitive` = UI text of this call is
/// masked (spec §6.3). `HasContentArgs` = at least one argument was masked as content (spec §6.4).
/// `Targetless` = the call had no target window to classify.</summary>
public sealed record LogScope(string Cid, string Tool, bool Sensitive, bool HasContentArgs, bool Targetless);

/// <summary>The current call's scope, for code that cannot read logger scopes (ToolResponse's `log`
/// pointer). Set by ToolCallLogFilter; flows with the async call.</summary>
public static class LogContext
{
    private static readonly AsyncLocal<LogScope?> s_current = new();
    public static LogScope? Current { get => s_current.Value; set => s_current.Value = value; }
}
