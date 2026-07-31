using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket handlers for the consent frames. Trust model: anything on the daemon's own
/// 0600 socket is the owner (same rule as HandleLocalSpawnAsync) — no further auth.
internal sealed class LaunchConsentIpc(
    LaunchConsentBroker broker, LaunchConsentStore store, ILogger<LaunchConsentIpc> logger) {

    public async Task HandleSubscribeAsync(Stream stream, CancellationToken ct) {
        var (id, reader) = broker.Subscribe();
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // EOF watcher: a subscriber that disappears must flip HasSubscriber promptly,
            // otherwise prompt-mode launches would wait the full timeout for nobody.
            var eof = Task.Run(async () => {
                try { while (await FrameCodec.ReadAsync(stream, cts.Token) is not null) { } }
                catch { }
                cts.Cancel();
            }, cts.Token);
            await foreach (var req in reader.ReadAllAsync(cts.Token)) {
                var json = JsonSerializer.Serialize(ToDto(req), ConsentIpcJsonContext.Default.ConsentPendingDto);
                await FrameCodec.WriteAsync(stream, LocalFrame.ConsentJson(FrameType.ConsentPending, json), cts.Token);
            }
        } catch (OperationCanceledException) {
        } finally {
            broker.Unsubscribe(id);
        }
    }

    public async Task HandleResolveAsync(string payload, Stream stream, CancellationToken ct) {
        ConsentAckDto ack;
        try {
            var dto = JsonSerializer.Deserialize(payload, ConsentIpcJsonContext.Default.ConsentResolveDto);
            if (dto is null || dto.Decision is not ("allow" or "deny")) {
                ack = new ConsentAckDto(false, "invalid resolve payload (decision must be allow|deny)");
            } else {
                string? saveError = null;
                if (dto.SaveRule is { } r) {
                    var current = store.Current;
                    var next = current with {
                        Rules = [.. current.Rules, new LaunchConsentRule(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor)] };
                    if (!store.TryReplace(next, out saveError))
                        logger.LogWarning("Consent save_rule rejected: {Error}", saveError);
                }
                var resolved = broker.TryResolve(dto.RequestId, dto.Decision == "allow");
                ack = resolved
                    ? new ConsentAckDto(saveError is null, saveError)
                    : new ConsentAckDto(false, "no pending consent request with that id");
            }
        } catch (JsonException) {
            ack = new ConsentAckDto(false, "malformed resolve payload");
        }
        await WriteAck(stream, ack, ct);
    }

    public async Task HandleRulesGetAsync(Stream stream, CancellationToken ct) {
        var p = store.Current;
        var dto = new ConsentPolicyDto(
            p.Default switch { LaunchConsentDefault.Deny => "deny", LaunchConsentDefault.Prompt => "prompt", _ => "allow" },
            p.PromptTimeoutSeconds,
            p.Rules.Select(r => new ConsentRuleDto(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor)).ToList());
        var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyDto);
        await FrameCodec.WriteAsync(stream, LocalFrame.ConsentJson(FrameType.ConsentRules, json), ct);
    }

    public async Task HandleRulesPutAsync(string payload, Stream stream, CancellationToken ct) {
        ConsentAckDto ack;
        try {
            var dto = JsonSerializer.Deserialize(payload, ConsentIpcJsonContext.Default.ConsentPolicyDto);
            if (dto is null) {
                ack = new ConsentAckDto(false, "malformed policy payload");
            } else {
                var def = dto.Default switch {
                    "deny" => LaunchConsentDefault.Deny, "prompt" => LaunchConsentDefault.Prompt,
                    "allow" => LaunchConsentDefault.Allow,
                    _ => (LaunchConsentDefault?)null };
                if (def is null) {
                    ack = new ConsentAckDto(false, "invalid default (allow|deny|prompt)");
                } else {
                    var next = new LaunchConsentPolicy(def.Value, dto.PromptTimeoutSeconds,
                        dto.Rules.Select(r => new LaunchConsentRule(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor)).ToList());
                    ack = store.TryReplace(next, out var error)
                        ? new ConsentAckDto(true, null) : new ConsentAckDto(false, error);
                }
            }
        } catch (JsonException) {
            ack = new ConsentAckDto(false, "malformed policy payload");
        }
        await WriteAck(stream, ack, ct);
    }

    static ConsentPendingDto ToDto(LaunchConsentPromptRequest r) =>
        new(r.RequestId, r.Requester, r.Kind, r.RepoPath, r.Vendor, r.RequestedAt, r.TimeoutSeconds);

    static Task WriteAck(Stream stream, ConsentAckDto ack, CancellationToken ct) {
        var json = JsonSerializer.Serialize(ack, ConsentIpcJsonContext.Default.ConsentAckDto);
        return FrameCodec.WriteAsync(stream, LocalFrame.ConsentJson(FrameType.ConsentAck, json), ct);
    }
}
