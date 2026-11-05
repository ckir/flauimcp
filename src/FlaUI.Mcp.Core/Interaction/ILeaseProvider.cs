// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;

namespace FlaUI.Mcp.Core.Interaction;

/// <summary>Reads the current lease and its last-write time (for the budget reset). Abstracted so the
/// guard's lease/expiry/budget-reset paths are testable without the filesystem or wall clock.</summary>
public interface ILeaseProvider
{
    /// <summary>The parsed current lease (null if absent/unparseable), and its file write time.</summary>
    InputLease? Read(out DateTime lastWriteUtc);
}
