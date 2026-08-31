using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Append-only JSONL audit of every consent decision (rule-matched and human), rendered by the
/// desktop app as the Activity feed and by `kcap daemon consent log`.
internal sealed class LaunchConsentDecisionLog(string stateDir, ILogger logger, long maxBytes = 1_048_576) {
    readonly OwnerOnlyJsonlLog _log = new(Path.Combine(stateDir, "consent-decisions.jsonl"), logger, maxBytes);

    public void Record(ConsentDecisionRecord rec) =>
        _log.Append(JsonSerializer.Serialize(rec, ConsentDecisionJsonContext.Default.ConsentDecisionRecord), rec.AgentId);
}
