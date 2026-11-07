// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

namespace FlaUI.Mcp.Core.Logging;

/// <summary>Logged raw: handles, refs, enum-like words, chords, timeouts, flags. See the structured
/// logging spec §6.1. An UNMARKED string is content and is hashed (fail closed).</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class LogStructuralAttribute : Attribute { }

/// <summary>Text that comes FROM the UI (titles, element names, automation ids, find queries). Logged raw
/// unless the call's target window is sensitive (spec §6.3).</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class LogUiTextAttribute : Attribute { }

/// <summary>On a tool method: when its `window` argument is omitted it acts on the current FOREGROUND
/// window, so the call's sensitivity is decided by the foreground process (spec §6.3).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LogForegroundTargetAttribute : Attribute { }
