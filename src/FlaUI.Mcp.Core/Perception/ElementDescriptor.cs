// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

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
    IReadOnlyList<int> IndexPath,
    bool Focused = false);
