using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Durable per-agent PID records under <c>{state-dir}/agents/</c>, so a restarted daemon can reap a
/// surviving child by exact identity (spec §6.4(2)). Written atomically (temp file + same-directory
/// rename). The filename is a SHA-256 of the agent id, NOT the id itself: the id crosses the SignalR
/// wire unconstrained, so using it directly in a path would let <c>..</c> / separators escape the
/// directory. The original id is preserved inside the JSON. Deleted only after confirmed death (caller's
/// rule); an unparseable/empty file is quarantined (renamed <c>.corrupt</c>) and never acted on.
/// </summary>
internal sealed class AgentPidRecordStore(string stateDir, ILogger logger) {
    readonly string _agentsDir = Path.Combine(stateDir, "agents");

    /// <summary>Atomically write (or overwrite) the record. Throws on I/O failure — the caller treats a
    /// write failure as a launch failure and tears the child down (§6.4(2a)).</summary>
    public void Write(AgentPidRecord record) {
        Directory.CreateDirectory(_agentsDir);

        var finalPath = PathFor(record.AgentId);
        var tempPath  = finalPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        File.WriteAllText(tempPath, JsonSerializer.Serialize(record, CapacitorJsonContext.Default.AgentPidRecord));
        File.Move(tempPath, finalPath, overwrite: true); // atomic same-directory rename
    }

    /// <summary>Delete an agent's record (idempotent — false if absent). Best-effort.</summary>
    public bool Delete(string agentId) {
        var path = PathFor(agentId);
        try {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        } catch (Exception ex) {
            logger.LogWarning(ex, "AgentPidRecordStore: failed to delete record for {AgentId}", agentId);
            return false;
        }
    }

    /// <summary>Whether a durable record exists for this agent id — one existence check, no read and no
    /// parse. This is the second half of the un-sequenced stop-admission probe (spec §3.3), which the
    /// sequenced processor evaluates inside its own critical section, so it must stay this cheap: never
    /// widen it to <see cref="ReadAll"/>. A record-backed id is a REAL stop target — the stop path reaps a
    /// prior incarnation's survivor by exact identity — not a no-op, so admission has to see it.
    ///
    /// <para>Asymmetric on failure, deliberately: a transient filesystem fault answers <c>false</c>, so that
    /// one survivor stop is DROPPED rather than queued. That is safe only because the server's
    /// retry-until-gone stop lane re-sends for an agent it still tracks; the opposite bias (answering
    /// <c>true</c> on a fault) would admit every unknown id for as long as the filesystem kept
    /// hiccupping.</para></summary>
    public bool Exists(string agentId) {
        try {
            return File.Exists(PathFor(agentId));
        } catch (Exception ex) {
            logger.LogDebug(ex, "AgentPidRecordStore: existence check failed for {AgentId}", agentId);
            return false;
        }
    }

    /// <summary>All parseable leftover records. An unparseable or agent-id-less file is renamed
    /// <c>.corrupt</c> (retained for the operator, never acted on) and excluded.</summary>
    public IReadOnlyList<AgentPidRecord> ReadAll() {
        if (!Directory.Exists(_agentsDir)) return [];

        var results = new List<AgentPidRecord>();

        foreach (var path in Directory.EnumerateFiles(_agentsDir, "*.json")) {
            AgentPidRecord record;
            try {
                record = JsonSerializer.Deserialize(File.ReadAllText(path), CapacitorJsonContext.Default.AgentPidRecord);
            } catch (Exception ex) {
                logger.LogWarning(ex, "AgentPidRecordStore: unparseable record {Path}; quarantining as .corrupt", path);
                TryQuarantine(path);
                continue;
            }

            if (string.IsNullOrEmpty(record.AgentId)) {
                logger.LogWarning("AgentPidRecordStore: record {Path} has no agent id; quarantining as .corrupt", path);
                TryQuarantine(path);
                continue;
            }

            // A parseable record whose `start_identity` deserialized to NULL (the wire had an
            // explicit `null`, which System.Text.Json happily binds to the non-nullable positional
            // parameter) is malformed — quarantine it. Guard BEFORE the .Length checks below so a
            // null token can never NRE and abort the whole sweep (OrphanReaper enumerates ReadAll()
            // directly, so one bad record must not take down reaping for every other agent).
            if (record.StartIdentity is null) {
                logger.LogWarning("AgentPidRecordStore: record {Path} has a null start_identity; quarantining as .corrupt", path);
                TryQuarantine(path);
                continue;
            }

            var token = record.StartIdentity;

            // An UNKNOWN numeric identity_kind (e.g. a corrupt `identity_kind: 99`, or a value a
            // future daemon writes) deserializes to an out-of-range enum that is NEITHER Present
            // NOR IdentityUnavailable, so it slips past BOTH consistency predicates below and would
            // be treated as a valid new shape. Reject any undefined value outright. NOTE: a LEGACY
            // record with no identity_kind key decodes to Present (the zero value), which IS defined
            // — so this never fires on the backward-compat path.
            if (!Enum.IsDefined(record.IdentityKind)) {
                logger.LogWarning(
                    "AgentPidRecordStore: record {Path} has an unknown identity_kind ({Kind}); quarantining as .corrupt",
                    path, (int)record.IdentityKind);
                TryQuarantine(path);
                continue;
            }

            // M1-A (spec §4.3): the only rejected shapes are NEW-schema-inconsistent combinations —
            // Present claiming a comparable identity with an empty token, or IdentityUnavailable
            // claiming NO comparable identity while still carrying a nonempty one. A LEGACY record
            // (no identity_kind key at all) always decodes as Present (PidIdentityKind's zero value)
            // and is never rejected here, however old its token scheme.
            var inconsistent =
                (record.IdentityKind == PidIdentityKind.Present && token.Length == 0) ||
                (record.IdentityKind == PidIdentityKind.IdentityUnavailable && token.Length != 0);

            if (inconsistent) {
                logger.LogWarning(
                    "AgentPidRecordStore: record {Path} has an inconsistent identity_kind/start_identity combination ({Kind}, token length {Len}); quarantining as .corrupt",
                    path, record.IdentityKind, record.StartIdentity.Length);
                TryQuarantine(path);
                continue;
            }

            results.Add(record);
        }

        return results;
    }

    // Hash the (untrusted) agent id into the filename so no id can escape _agentsDir via path separators.
    string PathFor(string agentId) => Path.Combine(_agentsDir, SafeName(agentId) + ".json");

    static string SafeName(string agentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId ?? ""))).ToLowerInvariant();

    void TryQuarantine(string path) {
        try { File.Move(path, path + ".corrupt", overwrite: true); }
        catch (Exception ex) { logger.LogWarning(ex, "AgentPidRecordStore: failed to quarantine {Path}", path); }
    }
}
