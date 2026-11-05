// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

namespace FlaUI.Mcp.Core.Windows;

/// <summary>An opaque per-server window handle id like "w1". Namespaced, monotonic.</summary>
public readonly record struct WindowHandle(string Id)
{
    public override string ToString() => Id;
}
