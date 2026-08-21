using System.Text;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Accumulates codex app-server per-item deltas (<c>item/agentMessage/delta</c>, reasoning deltas,
/// <c>item/commandExecution/outputDelta</c>, …) into the CUMULATIVE content-so-far that each ephemeral
/// envelope carries. §2.4 fixes the contract: ephemeral payloads are the item's whole accumulated
/// content (idempotent replacement at the viewer), never an increment — so a dropped or duplicated
/// ephemeral is harmless, and the canonical <c>item/completed</c> snapshot supersedes it. There is no
/// increment reassembly anywhere; <see cref="Complete"/> drops the item's transient state once its
/// authoritative snapshot has been mapped.
///
/// <para>Not thread-safe: driven from the single notification-handling path.</para>
/// </summary>
internal sealed class CodexEphemeralAccumulator {
    readonly Dictionary<string, StringBuilder> _byItem = new(StringComparer.Ordinal);

    /// <summary>Appends a delta to the item's buffer and returns the accumulated content so far — the
    /// payload of the ephemeral envelope for this item.</summary>
    public string Accumulate(string itemId, string chunk) {
        if (!_byItem.TryGetValue(itemId, out var sb)) {
            sb = new StringBuilder();
            _byItem[itemId] = sb;
        }
        sb.Append(chunk);
        return sb.ToString();
    }

    /// <summary>Drops the item's transient buffer — called once the canonical completed envelope for it
    /// has been produced (that snapshot finalizes the viewer's state for the item).</summary>
    public void Complete(string itemId) => _byItem.Remove(itemId);

    /// <summary>Number of items with live transient state (for the forward buffer's eviction bookkeeping
    /// and tests).</summary>
    public int ActiveItems => _byItem.Count;
}
