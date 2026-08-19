using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// Identity of the daemon process that answered hello, correlated against the first snapshot.
/// Pid/InstanceId are null when the hello reply predates those fields
/// (pre-slice daemon) — a null pair here is NOT evidence of a mismatch, only of an old daemon;
/// see RunCycleAsync for the correlation rule that decides whether Connected is even reached.
public sealed record ConnectedIdentity(int? Pid, string? InstanceId, string DaemonName, string DaemonVersion);

/// Typed events from LocalControlClient.RunAsync. BCL-only — this file is compiled into the
/// NativeAOT CLI/daemon, so no Rx types may appear on this surface.
public abstract record LocalControlEvent {
    public sealed record Connecting : LocalControlEvent;

    /// Carries the FIRST validated snapshot: a consumer that gates rendering on Connected can
    /// never observe the connected state while holding only a previous incarnation's data.
    /// Identity is additive — null only if a caller builds this record without
    /// going through RunCycleAsync; the client itself always populates it once it decides to
    /// yield Connected at all (see the hello/snapshot correlation invariant there).
    public sealed record Connected(
        IReadOnlyList<string>? Capabilities, DaemonStatusDto FirstSnapshot,
        ConnectedIdentity? Identity = null) : LocalControlEvent;

    /// Reason is "daemon_unreachable" (transport/unresponsive) or "daemon_incompatible"
    /// (protocol evidence — a heuristic that background retries self-correct). DaemonVersion is
    /// the hello reply's version when one was read before the failure (null otherwise, e.g. a
    /// transport failure or a mid-stream break, which never re-reads hello).
    public sealed record Unreachable(string Reason, string? DaemonVersion = null) : LocalControlEvent;

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
public sealed class LocalControlClient(DaemonStore store, string daemonName, TimeProvider? time = null) {
    readonly TimeProvider _time = time ?? TimeProvider.System;

    // Internal test seams: production always runs these defaults, so no public validation
    // contract exists — an invalid value is a test-authoring bug.
    internal TimeSpan[] RetryDelays { get; set; } = [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    internal TimeSpan ConnectTimeout       { get; set; } = TimeSpan.FromSeconds(5); // each dial: hello AND subscribe
    internal TimeSpan HelloReplyTimeout    { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan FirstSnapshotTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// Test seam: runs after RunCycleAsync returns a SUCCESSFUL outcome but before the ct
    /// checkpoint below, so a test can cancel exactly at that boundary deterministically.
    internal Action? OnCycleSucceededForTest { get; set; }

    const string Unreach = "daemon_unreachable";
    const string Incompat = "daemon_incompatible";

    /// One attach-cycle outcome: either a classified failure reason (with the hello reply's
    /// DaemonVersion, when one was read before the failure), or success carrying the negotiated
    /// capabilities, the first validated snapshot, the correlated hello identity, and the
    /// still-open subscribe stream for the caller to keep reading from.
    sealed record CycleOutcome(string? Reason, string? DaemonVersion, IReadOnlyList<string>? Capabilities, DaemonStatusDto? FirstSnapshot, NetworkStream? Stream, ConnectedIdentity? Identity) {
        public static CycleOutcome Failed(string reason, string? daemonVersion = null) => new(reason, daemonVersion, null, null, null, null);
        public static CycleOutcome Ok(IReadOnlyList<string> caps, DaemonStatusDto first, NetworkStream stream, ConnectedIdentity? identity) => new(null, null, caps, first, stream, identity);
    }

    public async IAsyncEnumerable<LocalControlEvent> RunAsync([EnumeratorCancellation] CancellationToken ct) {
        yield return new LocalControlEvent.Connecting();

        // transition-only: the last yielded Unreachable (reason, DaemonVersion) pair, cleared on
        // Connected — a version change re-emits even when the reason stays the same (spec
        // decision 6), so the dedupe key is the PAIR, not the reason alone.
        (string Reason, string? DaemonVersion)? last = null;
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
                var key = (reason, cycle.DaemonVersion);
                if (key != last) {
                    last = key;
                    yield return new LocalControlEvent.Unreachable(reason, cycle.DaemonVersion);
                }
                if (!await WaitBackoffAsync()) yield break;
                continue;
            }

            OnCycleSucceededForTest?.Invoke();
            // Shared cancellation checkpoint (mirrors the failed-cycle one above): a cycle that
            // succeeded only because cancellation raced ahead of the caller noticing must never
            // surface Connected — the enumeration is being torn down, not handed a live stream.
            if (ct.IsCancellationRequested) {
                await DisposeQuietly(cycle.Stream);
                yield break;
            }

            // The cycle SUCCEEDED only because the first VALID snapshot arrived — reset the
            // backoff schedule and yield Connected carrying that very snapshot (§4.1/§4.3).
            last = null;
            attempt = 0;

            var sub = cycle.Stream!;
            string streakReason;
            // finally: enumerator disposal (break/Take(n)/DisposeAsync while suspended at a
            // yield below) must still release `sub` — code placed after the loop never runs.
            try {
                yield return new LocalControlEvent.Connected(cycle.Capabilities, cycle.FirstSnapshot!, cycle.Identity);

                while (true) {
                    var next = await ReadSnapshotAsync(sub, timeout: null, ct);
                    if (ct.IsCancellationRequested) yield break;
                    if (next.Reason is { } r) { streakReason = r; break; }
                    yield return new LocalControlEvent.Status(next.Snapshot!);
                }
            } finally {
                await DisposeQuietly(sub);
            }

            // Cancellation can land DURING that disposal (the finally above suspends on an
            // async await), so re-check here too — the same "never surface data after
            // cancellation won the race" rule as every other checkpoint in this method.
            if (ct.IsCancellationRequested) yield break;

            // The streak-break read (ReadSnapshotAsync on the established stream) never re-reads
            // hello, so it has no DaemonVersion of its own — the dedupe key's version half is
            // always null here.
            var streakKey = (streakReason, (string?)null);
            if (streakKey != last) {
                last = streakKey;
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
        string? daemonVersion = null;    // captured from the hello reply, if one was ever read
        HelloReplyDto? hello = null;     // retained (not discarded) for identity correlation below
        try {
            IReadOnlyList<string> caps;
            await using (var helloConn = await DialAsync(ct)) {
                await FrameCodec.WriteAsync(helloConn, new LocalFrame(FrameType.Hello), ct);
                using var helloTimeoutCts = new CancellationTokenSource(HelloReplyTimeout, _time);
                using var helloLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, helloTimeoutCts.Token);
                var reply = await FrameCodec.ReadAsync(helloConn, helloLinkedCts.Token);
                if (reply is null) return CycleOutcome.Failed(Incompat);            // hello-then-EOF heuristic
                if (reply.Type != FrameType.HelloReply) return CycleOutcome.Failed(Incompat); // Error/unexpected type
                hello = JsonSerializer.Deserialize(reply.Text, HelloIpcJsonContext.Default.HelloReplyDto);
                daemonVersion = hello?.DaemonVersion;                              // captured BEFORE the caps gate
                caps = hello?.Capabilities ?? [];                                  // null ⇒ empty
                if (!caps.Contains("status/1")) return CycleOutcome.Failed(Incompat, daemonVersion);
            }

            sub = await DialAsync(ct);
            await FrameCodec.WriteAsync(sub, new LocalFrame(FrameType.StatusSubscribe), ct);
            var first = await ReadSnapshotAsync(sub, FirstSnapshotTimeout, ct);
            if (first.Reason is { } r0) {
                await DisposeQuietly(sub);
                return CycleOutcome.Failed(r0, daemonVersion);
            }

            // Correlation invariant: only when BOTH the hello reply and the
            // first snapshot's daemon info carry pid AND instance_id, and those pairs disagree,
            // is this classified daemon_incompatible — Connected must never be yielded on a
            // mismatch. Either side lacking the fields (a pre-slice daemon on one leg) infers
            // nothing: Identity is still populated from hello alone, so old daemons stay
            // attachable and the consumer decides what an identity-less Connected means.
            var daemonInfo = first.Snapshot!.Daemon;
            if (hello?.Pid is { } helloPid && hello.InstanceId is { } helloInstance &&
                    daemonInfo.Pid is { } snapshotPid && daemonInfo.InstanceId is { } snapshotInstance &&
                    (helloPid != snapshotPid || helloInstance != snapshotInstance)) {
                await DisposeQuietly(sub);
                return CycleOutcome.Failed(Incompat, daemonVersion);
            }

            var identity = hello is null ? null
                : new ConnectedIdentity(hello.Pid, hello.InstanceId, hello.DaemonName, hello.DaemonVersion);
            return CycleOutcome.Ok(caps, first.Snapshot!, sub, identity);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            await DisposeQuietly(sub);
            return CycleOutcome.Failed(Unreach); // reason is moot: RunAsync's ct checkpoint fires first
        } catch (Exception ex) {
            await DisposeQuietly(sub);
            return CycleOutcome.Failed(Classify(ex), daemonVersion);
        }
    }

    // Linked-CTS timeout (not WaitAsync): WaitAsync abandons ConnectAsync/ReadAsync on expiry
    // rather than cancelling it, leaving the operation running against a stream we're about to
    // dispose. Passing the linked token INTO the operation actually cancels it at the deadline
    // while staying TimeProvider-driven for tests.
    async Task<NetworkStream> DialAsync(CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try {
            using var timeoutCts = new CancellationTokenSource(ConnectTimeout, _time);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(daemonName)), linkedCts.Token);
            return new NetworkStream(sock, ownsSocket: true);
        } catch { sock.Dispose(); throw; }
    }

    /// One frame → validated snapshot, or a classified failure reason. Timeout applies only
    /// to the first snapshot; the established stream is legitimately quiet (no idle timeout).
    /// See <see cref="DialAsync"/> for why the timeout is a linked token, not WaitAsync.
    async Task<(DaemonStatusDto? Snapshot, string? Reason)> ReadSnapshotAsync(
            NetworkStream s, TimeSpan? timeout, CancellationToken ct) {
        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t, _time) : null;
        using var linkedCts = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try {
            var frame = await FrameCodec.ReadAsync(s, linkedCts?.Token ?? ct);
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
