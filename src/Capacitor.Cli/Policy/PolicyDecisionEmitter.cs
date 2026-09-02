namespace Capacitor.Cli.Policy;

using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Policy;

internal sealed class PolicyDecisionEmitter(ConfigRoot config, ProfileContext profiles) {
    public async Task EmitAsync(PolicyDecisionEventV1 evt, PolicySnapshot snapshot) {
        try {
            var spool = new HookSpool(config);
            var poster = new AgentHookPoster(config, profiles);
            await EnsureSnapshotUploadedAsync(poster, spool, evt.SessionId, snapshot);
            var body = JsonSerializer.Serialize(evt, CapacitorJsonContext.Default.PolicyDecisionEventV1);
            await poster.PostOrSpoolAsync("policy-decision", body, evt.Vendor, spool, evt.SessionId, "policy-decision");
        }
        catch { }
    }

    async Task EnsureSnapshotUploadedAsync(AgentHookPoster poster, HookSpool spool, string sessionId, PolicySnapshot snapshot) {
        var marker = config.Path("policy", "uploaded", $"{sessionId}-{snapshot.Id[..Math.Min(16, snapshot.Id.Length)]}");
        if (File.Exists(marker)) return;
        var body = JsonSerializer.Serialize(PolicyWire.ToUpload(sessionId, snapshot),
            CapacitorJsonContext.Default.PolicySnapshotUploadV1);
        var outcome = await poster.PostOrSpoolAsync("policy-snapshot", body, "policy", spool, sessionId, "policy-snapshot");
        if (outcome is HookPostOutcome.Posted or HookPostOutcome.Spooled) {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "");
        }
    }
}
