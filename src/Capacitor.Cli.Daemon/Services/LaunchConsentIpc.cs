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
                // The enclosing `using` can dispose this CTS (method already returned via the
                // ReadAllAsync loop completing some other way) before this fire-and-forget task
                // gets here — Cancel() on an already-disposed CTS throws ObjectDisposedException,
                // which must not become an unobserved-task-exception crash.
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
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
            // STJ source-gen does NOT enforce non-nullable members on missing/null JSON fields —
            // a syntactically valid payload missing "request_id" (or a save_rule missing "action")
            // deserializes fine with the field left null, and would otherwise reach
            // broker.TryResolve(null, ...) / a null rule Action past this point. Guard the shape
            // explicitly so a malformed-but-parseable payload acks false instead of throwing
            // (an uncaught exception here drops the connection with no ConsentAck at all).
            if (dto is null || string.IsNullOrEmpty(dto.RequestId) || dto.Decision is not ("allow" or "deny")
                    || (dto.SaveRule is { } saveRuleShape && saveRuleShape.Action is null)) {
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
                // Ok reflects the RESOLUTION outcome only — whether the caller's decision was
                // applied to a still-pending request. A rejected save_rule is a secondary,
                // partial failure: it must not be conflated with "no pending request with that
                // id" (Ok=false), so it rides along as Error even when Ok=true. See ConsentAckDto.
                var resolved = broker.TryResolve(dto.RequestId, dto.Decision == "allow");
                ack = resolved
                    ? new ConsentAckDto(true, saveError)
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
            // As above: a syntactically valid payload missing "rules" (or "default", or a rule's
            // "action") deserializes with that member left null despite the non-nullable C#
            // declaration — .Select(...) on a null Rules list would throw ArgumentNullException,
            // uncaught, dropping the connection with no ConsentAck. Guard the shape explicitly.
            // A rules ARRAY ELEMENT can also be null (e.g. "rules":[null]) — still valid JSON,
            // still deserializes without throwing — so check for a null element before touching
            // r.Action on it, or this throws an uncaught NullReferenceException instead.
            if (dto is null || dto.Default is null || dto.Rules is null || dto.Rules.Any(r => r is null || r.Action is null)) {
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
