// src/FlaUI.Mcp.Core/Perception/Selector.cs
using FlaUI.Mcp.Core.Errors;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>A stable, identity-based target for an interaction tool, resolved fresh at action time
/// (Phase 10 #2). Exactly-one-of {ref | selector} is enforced at the tool boundary. IgnoreCase defaults
/// TRUE (ergonomic); set false to disambiguate a genuine Submit/submit collision. Scope is an eN ref that
/// narrows the search to that element's subtree (resolved under existing ref rules first).</summary>
public sealed record Selector(
    string? AutomationId = null,
    string? Name = null,
    string NameMatch = "eq",
    string? ControlType = null,
    string? Scope = null,
    bool IgnoreCase = true)
{
    /// <summary>Fail-closed at the tool layer BEFORE any UIA walk: a selector with no material field
    /// ({automationId,name,controlType} all absent) is rejected rather than translated into a whole-tree
    /// walk. An unparseable ControlType is rejected here too.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AutomationId)
            && string.IsNullOrWhiteSpace(Name)
            && string.IsNullOrWhiteSpace(ControlType))
            throw new ToolException(ToolErrorCode.InvalidArguments,
                "selector needs at least one of automationId / name / controlType.",
                "add a material field (automationId is the most stable), or use a ref from desktop_snapshot");

        if (!string.IsNullOrWhiteSpace(ControlType)
            && !FindQuerySpec.TryParseControlType(ControlType, out _))
            throw new ToolException(ToolErrorCode.InvalidArguments,
                $"selector controlType '{ControlType}' is not a known UIA ControlType.",
                "use a UIA ControlType name, e.g. Button, Edit, ListItem");
    }

    /// <summary>Map to the shared FindQuery (EnabledOnly is not a selector dimension - always false).</summary>
    public FindQuery ToFindQuery() => new(AutomationId, Name, NameMatch, ControlType, EnabledOnly: false, IgnoreCase);
}
