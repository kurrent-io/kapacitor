using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket handler for StatusSubscribe: one full DaemonStatusDto snapshot immediately,
/// then a debounced re-push whenever the change generation advances past this connection's
/// cursor. Full snapshots + per-connection cursors mean a missed pulse can never desync a
/// client, and a slow subscriber delays only itself. Trust model: same 0600-socket owner
/// trust as every other local frame.
internal sealed class DaemonStatusIpc(
    DaemonConfig config, AgentOrchestrator orchestrator, ServerConnection connection,
    DaemonStatusNotifier notifier) {

    /// Coalesces a pulse burst into one trailing snapshot. A tuning constant, not a wire
    /// contract; tests shrink it.
    internal TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(250);

    int _subscribers;
    internal int ActiveSubscribersForTest => Volatile.Read(ref _subscribers);

    /// Test seam: runs between materializing a snapshot and pushing it, so a test can land a
    /// mutation exactly at the cursor/snapshot boundary deterministically.
    internal Action? AfterSnapshotForTest { get; set; }

    public async Task HandleSubscribeAsync(Stream stream, CancellationToken ct) {
        Interlocked.Increment(ref _subscribers);
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // EOF watcher: a vanished subscriber must be reaped promptly (same discipline as
            // ConsentSubscribe), or the daemon would keep serializing snapshots for nobody.
            _ = Task.Run(async () => {
                try { while (await FrameCodec.ReadAsync(stream, cts.Token) is not null) { } }
                catch { }
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }, cts.Token);

            while (true) {
                var seen = notifier.Version; // cursor BEFORE snapshotting: a mutation during
                var json = Snapshot();       // the snapshot/push advances Version past `seen`
                AfterSnapshotForTest?.Invoke();
                await FrameCodec.WriteAsync(stream, LocalFrame.StatusJson(FrameType.DaemonStatus, json), cts.Token);
                await notifier.WaitBeyondAsync(seen, cts.Token);
                await Task.Delay(Debounce, cts.Token);
            }
        } catch (OperationCanceledException) {
            // subscriber EOF or daemon shutdown — either way the connection just closes
        } catch (IOException) {
            // A vanished subscriber is normal lifecycle for a long-lived subscription, not a
            // fault: FrameCodec.WriteAsync throws IOException (EndOfStreamException included —
            // it derives from IOException) when the client disconnects mid-push. Absorb it here
            // so LocalControlServer's generic catch doesn't log a routine disconnect at Warning.
        } catch (SocketException) {
            // Same as above, for the underlying transport signaling the disconnect instead of
            // the stream wrapper.
        } finally {
            Interlocked.Decrement(ref _subscribers);
        }
    }

    string Snapshot() {
        var agents = orchestrator.SnapshotAgentsForStatus();
        // Same predicate as the orchestrator's ActiveCount, applied to the SAME materialized
        // array — the count and the array can never disagree within one payload.
        var active = agents.Count(a => a.Status is "Starting" or "Running");
        var dto = new DaemonStatusDto(
            new DaemonInfoDto(
                config.Name, DaemonRunner.ResolveDaemonVersion(), config.ServerUrl,
                ConnectionText(connection.HubState), config.MaxConcurrentAgents, active,
                Environment.ProcessId, config.InstanceId),
            agents);
        return JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);
    }

    /// Wire spelling of <see cref="HubConnectionState"/>. Internal (not private) so
    /// <c>DaemonStatusIpcTests</c> can pin all four spellings directly — a pure static switch is
    /// smaller to test this way than standing up a HubState test double through the full snapshot.
    internal static string ConnectionText(HubConnectionState s) => s switch {
        HubConnectionState.Connected    => "connected",
        HubConnectionState.Connecting   => "connecting",
        HubConnectionState.Reconnecting => "reconnecting",
        _                               => "disconnected",
    };
}
