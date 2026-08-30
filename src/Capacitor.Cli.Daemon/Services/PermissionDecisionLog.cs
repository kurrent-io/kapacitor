using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Append-only JSONL audit of every settled, attributed permission request.
internal sealed class PermissionDecisionLog(string stateDir, ILogger logger, long maxBytes = 1_048_576) {
    readonly OwnerOnlyJsonlLog _log = new(Path.Combine(stateDir, "permission-decisions.jsonl"), logger, maxBytes);

    public void Record(PermissionDecisionRecord rec) =>
        _log.Append(JsonSerializer.Serialize(rec, PermissionDecisionJsonContext.Default.PermissionDecisionRecord), rec.AgentId);
}
