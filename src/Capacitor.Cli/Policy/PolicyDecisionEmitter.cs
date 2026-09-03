namespace Capacitor.Cli.Policy;

using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;

/// <summary>
/// Appends policy events to the hook spool, where the throttled drain that carries every other
/// lifecycle event picks them up. Nothing is posted inline: a decision seam runs under a 5s hook
/// ceiling and the vendor acts on the seam's stdout only once the process exits, so a round trip
/// here could outlive the hook and lose a deny that had already been written.
/// </summary>
internal sealed class PolicyDecisionEmitter(ConfigRoot config) {
    /// <param name="snapshot">Null only when the decision names no resolvable snapshot — a failure
    /// that never got one — so there is nothing to upload alongside it.</param>
    public Task EmitAsync(PolicyDecisionEventV1 evt, PolicySnapshot? snapshot) {
        try {
            var spool = new HookSpool(config);
            // Snapshot first: a decision names a snapshot id the server cannot resolve on its own,
            // and the spool delivers a session's entries in arrival order.
            if (snapshot is not null) EnsureSnapshotSpooled(spool, evt.SessionId, snapshot);
            var body = JsonSerializer.Serialize(evt, CapacitorJsonContext.Default.PolicyDecisionEventV1);
            spool.Append(evt.SessionId, "policy-decision", body);
        }
        catch { }

        return Task.CompletedTask;
    }

    void EnsureSnapshotSpooled(HookSpool spool, string sessionId, PolicySnapshot snapshot) {
        // Sanitized like the snapshot and journal keys: a raw id carrying a path separator would
        // put the marker in a nested directory the session-end eviction never looks in.
        var marker = config.Path("policy", "uploaded",
            $"{PolicySnapshotStore.Sanitize(sessionId)}-{snapshot.Id[..Math.Min(16, snapshot.Id.Length)]}");
        if (File.Exists(marker)) return;
        var body = JsonSerializer.Serialize(PolicyWire.ToUpload(sessionId, snapshot),
            CapacitorJsonContext.Default.PolicySnapshotUploadV1);
        // The marker may only be written once the append actually persisted, or a failed write would
        // suppress every later attempt and leave the decisions unresolvable.
        if (!spool.Append(sessionId, "policy-snapshot", body)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "");
    }
}
