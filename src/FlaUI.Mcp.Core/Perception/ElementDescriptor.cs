using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core.Perception;

/// <summary>Option-C element descriptor: re-resolvable across UIA tree mutation.
/// RuntimeId is an ephemeral fast-path only; AutomationId under the nearest stable
/// ancestor is the primary re-resolution key.</summary>
public sealed record ElementDescriptor(
    IReadOnlyList<int> RuntimeId,
    ControlType ControlType,
    string AutomationId,
    string Name,
    string? AncestorAutomationId,
    IReadOnlyList<int> IndexPath);
