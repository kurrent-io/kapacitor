using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Capacitor.Cli.Core.LocalIpc;

/// Status: "stopped" | "failed" | "skipped" (StopAck vocabulary) or "error" (daemon Error
/// frame; Error carries its display text). Ok is true only for "stopped".
public sealed record StopAgentResult(bool Ok, string Status, string? Error);

/// Reason ∈ daemon_unreachable | daemon_rejected | unexpected_reply | timed_out (stable
/// identifiers, not user copy — spec §10).
public sealed class LocalControlOpsException(string reason, string message) : Exception(message) {
    public string Reason { get; } = reason;
}

public interface ILocalControlOps {
    Task<StopAgentResult>  StopAgentAsync(string agentId, bool force, CancellationToken ct);
    Task<ConsentPolicyDto> GetConsentPolicyAsync(CancellationToken ct);
    Task<ConsentAckDto>    PutConsentPolicyAsync(ConsentPolicyDto policy, CancellationToken ct);
    Task<ConsentAckDto>    PutConsentPolicyV2Async(ConsentPolicyPutV2Dto put, CancellationToken ct);
    Task<ConsentAckDto>    ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct);
    Task<PermissionAckDto> ResolvePermissionAsync(PermissionResolveDto resolve, CancellationToken ct);
}

/// One-shot Core IPC operations behind a fresh socket per call — no Hello negotiation (callers
/// already know daemon capabilities from LocalControlClient's Connected/AttachStatus), no
/// persistent connection. Mirrors the CLI's existing socket usage (AgentCommand.SendStopAsync,
/// DaemonConsentCommand's GetPolicyAsync/PutPolicyAsync) so the app shares the same wire
/// behavior without depending on CLI command code. See design spec §10.
public sealed class LocalControlOps(DaemonStore store, string daemonName, TimeProvider? time = null) : ILocalControlOps {
    readonly TimeProvider _time = time ?? TimeProvider.System;

    // Internal seams for tests (same pattern as LocalControlClient):
    internal TimeSpan ConnectTimeout      = TimeSpan.FromSeconds(5);
    internal TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    internal TimeSpan StopReplyTimeout    = TimeSpan.FromSeconds(40); // StopAck lands only after graceful stop (~25s worst case)

    const string DaemonUnreachable = "daemon_unreachable";
    const string DaemonRejected    = "daemon_rejected";
    const string UnexpectedReply   = "unexpected_reply";
    const string TimedOut          = "timed_out";

    public async Task<StopAgentResult> StopAgentAsync(string agentId, bool force, CancellationToken ct) {
        // An empty agentId is not "stop nothing" on the wire — the daemon's StopV2 handler
        // (AgentOrchestrator.LocalIpc.cs) reads it as stop-ALL. This is a Core API whose name
        // says "stop agent", so that footgun must never reach the socket.
        if (string.IsNullOrEmpty(agentId))
            throw new ArgumentException("agentId must not be empty — an empty id is interpreted by the daemon as stop-ALL", nameof(agentId));

        var reply = await ExchangeAsync(LocalFrame.StopV2(force, agentId), StopReplyTimeout, ct);
        return reply.Type switch {
            FrameType.StopAck => ParseStopAck(reply.Text, agentId),
            FrameType.Error   => new StopAgentResult(false, "error", reply.Text),
            _ => throw new LocalControlOpsException(UnexpectedReply, $"unexpected daemon response to stop ({reply.Type})"),
        };
    }

    public async Task<ConsentPolicyDto> GetConsentPolicyAsync(CancellationToken ct) {
        var reply = await ExchangeAsync(new LocalFrame(FrameType.ConsentRulesGet), ReplyTimeout, ct);
        switch (reply.Type) {
            case FrameType.ConsentRules:
                var dto = DeserializeOrThrow(reply.Text, ConsentIpcJsonContext.Default.ConsentPolicyDto, "malformed consent policy reply");
                if (!IsValidPolicy(dto)) throw new LocalControlOpsException(UnexpectedReply, "malformed consent policy reply");
                return dto!;
            case FrameType.Error:
                throw new LocalControlOpsException(DaemonRejected, reply.Text);
            default:
                throw new LocalControlOpsException(UnexpectedReply, $"unexpected daemon response to consent rules get ({reply.Type})");
        }
    }

    public async Task<ConsentAckDto> PutConsentPolicyAsync(ConsentPolicyDto policy, CancellationToken ct) {
        var json = JsonSerializer.Serialize(policy, ConsentIpcJsonContext.Default.ConsentPolicyDto);
        var reply = await ExchangeAsync(LocalFrame.ConsentJson(FrameType.ConsentRulesPut, json), ReplyTimeout, ct);
        switch (reply.Type) {
            case FrameType.ConsentAck:
                var ack = DeserializeOrThrow(reply.Text, ConsentIpcJsonContext.Default.ConsentAckDto, "malformed consent ack reply");
                if (ack is null) throw new LocalControlOpsException(UnexpectedReply, "malformed consent ack reply");
                return ack; // {} decodes to Ok=false, Error=null — returned as-is; presentation is the caller's job
            case FrameType.Error:
                throw new LocalControlOpsException(DaemonRejected, reply.Text);
            default:
                throw new LocalControlOpsException(UnexpectedReply, $"unexpected daemon response to consent rules put ({reply.Type})");
        }
    }

    public async Task<ConsentAckDto> PutConsentPolicyV2Async(ConsentPolicyPutV2Dto put, CancellationToken ct) {
        var json = JsonSerializer.Serialize(put, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
        var reply = await ExchangeAsync(LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ReplyTimeout, ct);
        switch (reply.Type) {
            case FrameType.ConsentAck:
                var ack = DeserializeOrThrow(reply.Text, ConsentIpcJsonContext.Default.ConsentAckDto, "malformed consent ack reply");
                if (ack is null) throw new LocalControlOpsException(UnexpectedReply, "malformed consent ack reply");
                return ack; // Ok=false (e.g. identity_mismatch) returned as-is — not an exception
            case FrameType.Error:
                throw new LocalControlOpsException(DaemonRejected, reply.Text);
            default:
                throw new LocalControlOpsException(UnexpectedReply, $"unexpected daemon response to consent rules put v2 ({reply.Type})");
        }
    }

    public async Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct) {
        var json = JsonSerializer.Serialize(resolve, ConsentIpcJsonContext.Default.ConsentResolveDto);
        // ConsentResolveV2, never the v1 frame: a v1 daemon must fail closed at its codec rather
        // than resolve by id without the identity check (spec §4.1).
        var reply = await ExchangeAsync(LocalFrame.ConsentJson(FrameType.ConsentResolveV2, json), ReplyTimeout, ct);
        switch (reply.Type) {
            case FrameType.ConsentAck:
                var ack = DeserializeOrThrow(reply.Text, ConsentIpcJsonContext.Default.ConsentAckDto, "malformed consent ack reply");
                if (ack is null) throw new LocalControlOpsException(UnexpectedReply, "malformed consent ack reply");
                return ack;
            case FrameType.Error:
                throw new LocalControlOpsException(DaemonRejected, reply.Text);
            default:
                throw new LocalControlOpsException(UnexpectedReply, $"unexpected daemon response to consent resolve ({reply.Type})");
        }
    }

    public async Task<PermissionAckDto> ResolvePermissionAsync(PermissionResolveDto resolve, CancellationToken ct) {
        var json  = JsonSerializer.Serialize(resolve, PermissionIpcJsonContext.Default.PermissionResolveDto);
        var reply = await ExchangeAsync(LocalFrame.PermissionJson(FrameType.PermissionResolve, json), ReplyTimeout, ct);
        switch (reply.Type) {
            case FrameType.PermissionAck:
                var ack = DeserializeOrThrow(reply.Text, PermissionIpcJsonContext.Default.PermissionAckDto, "malformed permission ack reply");
                if (ack is null) throw new LocalControlOpsException(UnexpectedReply, "malformed permission ack reply");
                return ack;
            case FrameType.Error:
                throw new LocalControlOpsException(DaemonRejected, reply.Text);
            default:
                throw new LocalControlOpsException(UnexpectedReply, $"unexpected daemon response to permission resolve ({reply.Type})");
        }
    }

    static T? DeserializeOrThrow<T>(string json, JsonTypeInfo<T> typeInfo, string errorMessage) {
        try { return JsonSerializer.Deserialize(json, typeInfo); }
        catch (JsonException) { throw new LocalControlOpsException(UnexpectedReply, errorMessage); }
    }

    /// A StopAck reply must carry exactly one line for the requested agent id, with exactly two
    /// tab-separated fields and a status in the StopAck vocabulary — anything else (missing,
    /// duplicated, malformed, or an unknown status token) is a protocol violation, not a result.
    static StopAgentResult ParseStopAck(string text, string agentId) {
        var lines = text.Length == 0 ? [] : text.Split('\n');
        string? status = null;
        var matches = 0;
        foreach (var line in lines) {
            var parts = line.Split('\t');
            if (parts.Length == 0 || parts[0] != agentId) continue;
            matches++;
            if (parts.Length == 2) status = parts[1];
        }
        if (matches != 1 || status is not ("stopped" or "failed" or "skipped"))
            throw new LocalControlOpsException(UnexpectedReply, $"malformed StopAck reply for {agentId}");
        return new StopAgentResult(status == "stopped", status, null);
    }

    /// STJ source-gen does not enforce non-nullable members on deserialize — the daemon's own
    /// receive path guards for exactly this (LaunchConsentIpc.HandleRulesPutAsync); this mirrors
    /// it on the read side so a caller can dereference every field of a returned policy.
    static bool IsValidPolicy(ConsentPolicyDto? dto) {
        if (dto is null) return false;
        if (dto.Default is not ("allow" or "deny" or "prompt")) return false;
        if (dto.PromptTimeoutSeconds < 1) return false;
        if (dto.Rules is null) return false;
        foreach (var r in dto.Rules) {
            if (r is null) return false;
            if (r.Action is not ("allow" or "deny")) return false;
        }
        return true;
    }

    /// One fresh socket: connect → write the request → read the single reply → close. Phase
    /// timeouts are linked CancellationTokenSources (the LocalControlClient pattern), never
    /// WaitAsync (which would abandon the socket op rather than cancel it). Catch precedence is
    /// pinned (spec §10): caller-token cancellation propagates as-is and is checked FIRST, so it
    /// can never be misreported as a timeout; EndOfStreamException is checked before the general
    /// IOException branch because it derives from IOException.
    async Task<LocalFrame> ExchangeAsync(LocalFrame request, TimeSpan replyTimeout, CancellationToken ct) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try {
            using (var connectTimeoutCts = new CancellationTokenSource(ConnectTimeout, _time))
            using (var connectLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeoutCts.Token))
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(daemonName)), connectLinkedCts.Token);

            await using var stream = new NetworkStream(sock, ownsSocket: false);
            await FrameCodec.WriteAsync(stream, request, ct);

            using var replyTimeoutCts = new CancellationTokenSource(replyTimeout, _time);
            using var replyLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, replyTimeoutCts.Token);
            var reply = await FrameCodec.ReadAsync(stream, replyLinkedCts.Token);
            if (reply is null) throw new LocalControlOpsException(UnexpectedReply, "daemon closed the connection without replying");
            return reply;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (OperationCanceledException) {
            throw new LocalControlOpsException(TimedOut, "timed out waiting for the daemon");
        } catch (EndOfStreamException ex) {
            throw new LocalControlOpsException(UnexpectedReply, ex.Message);
        } catch (InvalidDataException ex) { // undecodable frame (bad length prefix, unknown type byte)
            throw new LocalControlOpsException(UnexpectedReply, ex.Message);
        } catch (Exception ex) when (ex is IOException or SocketException) {
            throw new LocalControlOpsException(DaemonUnreachable, ex.Message);
        }
    }
}
