namespace FlaUI.Mcp.Core.Perception;

/// <summary>Pure decision for restoring the originally-active terminal tab (spec §5.2.9). You cannot
/// restore by a pre-switch ref (stale — §3.4) or by AutomationId (none — §5.2.7). Title is the only
/// pre-verifiable identity; ordinal is the fallback. Restore is best-effort with honest reporting.</summary>
public static class RestoreTarget
{
    public readonly record struct Result(int? SelectIndex, bool Restored, string Confidence);

    /// <param name="recordedTitle">Title of the tab that was active up front.</param>
    /// <param name="recordedOrdinal">Its 0-based index in the up-front enumeration.</param>
    /// <param name="wasTitleUnique">Whether that title was unique in the up-front enumeration.</param>
    /// <param name="freshTitles">Titles re-enumerated at restore time, in ordinal order.</param>
    /// <param name="freshSensitivities">DEF-4: a PARALLEL mask over <paramref name="freshTitles"/> — same
    /// length, same indexes. Null (the default) means "no partition", i.e. every tab is comparable, which
    /// is the pre-SP3 behaviour and what a server with no redaction rules produces.
    /// ⚠ It is a MASK, never a filtered list. SelectIndex is an index INTO freshTitles, the caller does
    /// fresh[idx], and recordedOrdinal is bounds-checked against freshTitles.Count — filtering would
    /// desynchronise both and select the WRONG tab, which is worse than the leak it closes.</param>
    /// <param name="recordedSensitivity">Sensitivity of the tab that was active up front. Only tabs with
    /// an equal Sensitivity are compared against it, which is what closes the restoreConfidence oracle:
    /// a visible tab structurally cannot collide with a protected one.</param>
    public static Result Resolve(string recordedTitle, int recordedOrdinal, bool wasTitleUnique,
        IReadOnlyList<string> freshTitles,
        IReadOnlyList<Sensitivity>? freshSensitivities = null,
        Sensitivity recordedSensitivity = default)
    {
        // A mask of the wrong length is a programming error, not a runtime condition. Degrade to the
        // no-partition behaviour rather than half-partitioning: a partly-applied mask would produce
        // wrong-but-plausible confidences, which is harder to notice than no partition at all.
        var mask = freshSensitivities is not null && freshSensitivities.Count == freshTitles.Count
            ? freshSensitivities : null;

        // Prefer a confident title match: only when the title WAS unique up front AND still resolves to
        // exactly one tab now (a concurrent add could duplicate it — then it is no longer confident).
        if (wasTitleUnique)
        {
            int found = -1, count = 0;
            for (int i = 0; i < freshTitles.Count; i++)
            {
                if (mask is not null && mask[i] != recordedSensitivity) continue; // DEF-4 partition
                if (string.Equals(freshTitles[i], recordedTitle, System.StringComparison.Ordinal))
                { found = i; count++; }
            }
            if (count == 1) return new Result(found, true, "high");
        }
        // Ordinal fallback (ambiguous title up front, or unique-but-not-uniquely-found now).
        if (recordedOrdinal >= 0 && recordedOrdinal < freshTitles.Count)
            return new Result(recordedOrdinal, true, "reduced");
        // Count shrank / ordinal out of range and no confident title: cannot restore — report honestly.
        return new Result(null, false, "none");
    }
}
