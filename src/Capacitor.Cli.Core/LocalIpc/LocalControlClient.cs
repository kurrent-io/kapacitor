using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// Typed events from LocalControlClient.RunAsync. BCL-only — this file is compiled into the
/// NativeAOT CLI/daemon, so no Rx types may appear on this surface.
public abstract record LocalControlEvent {
    public sealed record Connecting : LocalControlEvent;

    /// Carries the FIRST validated snapshot: a consumer that gates rendering on Connected can
    /// never observe the connected state while holding only a previous incarnation's data.
    public sealed record Connected(
        IReadOnlyList<string>? Capabilities, DaemonStatusDto FirstSnapshot) : LocalControlEvent;

    /// Reason is "daemon_unreachable" (transport/unresponsive) or "daemon_incompatible"
    /// (protocol evidence — a heuristic that background retries self-correct).
    public sealed record Unreachable(string Reason) : LocalControlEvent;

    public sealed record Status(DaemonStatusDto Snapshot) : LocalControlEvent;
}

/// Structural validity for DaemonStatus payloads: STJ source-gen leaves declared-non-nullable
/// members null on absent/null JSON, so the client validates before yielding — an app may
/// dereference every field of a yielded snapshot. Id uniqueness is load-bearing for keyed
/// diffing downstream.
internal static class DaemonStatusValidator {
    internal static bool IsValid(DaemonStatusDto? dto) {
        if (dto?.Daemon is not { } d || dto.Agents is not { } agents) return false;
        if (d.Name is null || d.Version is null || d.ServerUrl is null || d.Connection is null) return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in agents) {
            if (a is null || string.IsNullOrWhiteSpace(a.Id)) return false;
            if (a.Kind is null || a.Vendor is null || a.Status is null) return false;
            if (!seen.Add(a.Id)) return false;
        }
        return true;
    }
}

/// Self-healing typed client for the daemon's local control socket: connect → hello gate →
/// StatusSubscribe → snapshot stream, reconnecting with backoff. All failure is DATA (the
/// event stream); only cancellation ends the enumeration. See the state machine and
/// classification rules in the app-shell design spec §4 — every branch here is pinned there.
public sealed class LocalControlClient(string daemonName, TimeProvider? time = null) {
    readonly TimeProvider _time = time ?? TimeProvider.System;

    // Internal test seams: production always runs these defaults, so no public validation
    // contract exists — an invalid value is a test-authoring bug.
    internal TimeSpan[] RetryDelays { get; set; } = [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    internal TimeSpan ConnectTimeout       { get; set; } = TimeSpan.FromSeconds(5); // each dial: hello AND subscribe
    internal TimeSpan HelloReplyTimeout    { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan FirstSnapshotTimeout { get; set; } = TimeSpan.FromSeconds(10);

    const string Unreach = "daemon_unreachable";
    const string Incompat = "daemon_incompatible";

    /// One attach-cycle outcome: either a classified failure reason, or success carrying the
    /// negotiated capabilities, the first validated snapshot, and the still-open subscribe
    /// stream for the caller to keep reading from.
    sealed record CycleOutcome(string? Reason, IReadOnlyList<string>? Capabilities, DaemonStatusDto? FirstSnapshot, NetworkStream? Stream) {
        public static CycleOutcome Failed(string reason) => new(reason, null, null, null);
        public static CycleOutcome Ok(IReadOnlyList<string> caps, DaemonStatusDto first, NetworkStream stream) => new(null, caps, first, stream);
    }

    public async IAsyncEnumerable<LocalControlEvent> RunAsync([EnumeratorCancellation] CancellationToken ct) {
        yield return new LocalControlEvent.Connecting();

        string? lastReason = null; // transition-only: the last yielded Unreachable reason, cleared on Connected
        var attempt = 0;

        // Waits the current backoff slot on the injected clock and advances the schedule.
        // Returns false (never fabricating an event) if cancellation won the race.
        async Task<bool> WaitBackoffAsync() {
            var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
            attempt++;
            try { await Task.Delay(delay, _time, ct); return true; }
            catch (OperationCanceledException) { return false; }
        }

        while (!ct.IsCancellationRequested) {
            var cycle = await RunCycleAsync(ct);

            if (cycle.Reason is { } reason) {
                // Shared cancellation checkpoint (mirrors the connected-streak one below): a
                // cycle that failed BECAUSE cancellation won a race must never surface as data.
                if (ct.IsCancellationRequested) yield break;
                if (reason != lastReason) {
                    lastReason = reason;
                    yield return new LocalControlEvent.Unreachable(reason);
                }
                if (!await WaitBackoffAsync()) yield break;
                continue;
            }

            // The cycle SUCCEEDED only because the first VALID snapshot arrived — reset the
            // backoff schedule and yield Connected carrying that very snapshot (§4.1/§4.3).
            lastReason = null;
            attempt = 0;

            var sub = cycle.Stream!;
            string streakReason;
            // `sub` is held live across every yield below (Connected, then each Status). A
            // consumer that leaves the enumeration WITHOUT cancelling — break, Take(n), an
            // exception thrown from its own await-foreach body, or an explicit
            // DisposeAsync() — disposes this iterator while it's suspended at one of those
            // yields; only code inside an ENCLOSING try/finally still runs on that path (a
            // statement after the loop, like the old `await DisposeQuietly(sub);`, would be
            // skipped entirely). The close must therefore live in a finally wrapping every
            // yield that can observe `sub`, not after them.
            try {
                yield return new LocalControlEvent.Connected(cycle.Capabilities, cycle.FirstSnapshot!);

                while (true) {
                    var next = await ReadSnapshotAsync(sub, timeout: null, ct);
                    if (ct.IsCancellationRequested) yield break;
                    if (next.Reason is { } r) { streakReason = r; break; }
                    yield return new LocalControlEvent.Status(next.Snapshot!);
                }
            } finally {
                await DisposeQuietly(sub);
            }

            if (streakReason != lastReason) {
                lastReason = streakReason;
                yield return new LocalControlEvent.Unreachable(streakReason);
            }
            if (!await WaitBackoffAsync()) yield break;
        }
    }

    /// One full attach cycle: hello (one-shot connection) → gate on capabilities → subscribe
    /// (second connection) → first VALID snapshot. Never yields — all outcomes are returned as
    /// data so RunAsync owns every event-ordering decision at a single call site.
    async Task<CycleOutcome> RunCycleAsync(CancellationToken ct) {
        NetworkStream? sub = null;
        try {
            IReadOnlyList<string> caps;
            await using (var hello = await DialAsync(ct)) {
                await FrameCodec.WriteAsync(hello, new LocalFrame(FrameType.Hello), ct);
                var reply = await FrameCodec.ReadAsync(hello, ct).WaitAsync(HelloReplyTimeout, _time, ct);
                if (reply is null) return CycleOutcome.Failed(Incompat);            // hello-then-EOF heuristic
                if (reply.Type != FrameType.HelloReply) return CycleOutcome.Failed(Incompat); // Error/unexpected type
                var dto = JsonSerializer.Deserialize(reply.Text, HelloIpcJsonContext.Default.HelloReplyDto);
                caps = dto?.Capabilities ?? [];                                    // null ⇒ empty
                if (!caps.Contains("status/1")) return CycleOutcome.Failed(Incompat);
            }

            sub = await DialAsync(ct);
            await FrameCodec.WriteAsync(sub, new LocalFrame(FrameType.StatusSubscribe), ct);
            var first = await ReadSnapshotAsync(sub, FirstSnapshotTimeout, ct);
            if (first.Reason is { } r0) {
                await DisposeQuietly(sub);
                return CycleOutcome.Failed(r0);
            }

            return CycleOutcome.Ok(caps, first.Snapshot!, sub);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            await DisposeQuietly(sub);
            return CycleOutcome.Failed(Unreach); // reason is moot: RunAsync's ct checkpoint fires first
        } catch (Exception ex) {
            await DisposeQuietly(sub);
            return CycleOutcome.Failed(Classify(ex));
        }
    }

    async Task<NetworkStream> DialAsync(CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try {
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(LocalSocketPaths.Socket(daemonName)), ct)
                .AsTask().WaitAsync(ConnectTimeout, _time, ct);
            return new NetworkStream(sock, ownsSocket: true);
        } catch { sock.Dispose(); throw; }
    }

    /// One frame → validated snapshot, or a classified failure reason. Timeout applies only
    /// to the first snapshot; the established stream is legitimately quiet (no idle timeout).
    async Task<(DaemonStatusDto? Snapshot, string? Reason)> ReadSnapshotAsync(
            NetworkStream s, TimeSpan? timeout, CancellationToken ct) {
        try {
            var read = FrameCodec.ReadAsync(s, ct);
            var frame = timeout is { } t ? await read.WaitAsync(t, _time, ct) : await read;
            if (frame is null) return (null, Unreach);                            // clean EOF
            if (frame.Type != FrameType.DaemonStatus) return (null, Incompat);     // Error/unexpected type
            var dto = JsonSerializer.Deserialize(frame.Text, StatusIpcJsonContext.Default.DaemonStatusDto);
            return DaemonStatusValidator.IsValid(dto) ? (dto, null) : (null, Incompat);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (null, Unreach); }
        catch (Exception ex) { return (null, Classify(ex)); }
    }

    static string Classify(Exception ex) => ex switch {
        InvalidDataException or JsonException => Incompat,                        // protocol evidence
        _ => Unreach,                                                             // transport + catch-all
    };

    static async ValueTask DisposeQuietly(NetworkStream? s) {
        if (s is null) return;
        try { await s.DisposeAsync(); } catch { }
    }
}
