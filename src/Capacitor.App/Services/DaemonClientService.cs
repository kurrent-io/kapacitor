using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Services;

/// Rx/DynamicData adapter over LocalControlClient.RunAsync: owns the single live attach
/// enumeration, publishes the atomic AttachStatus + Snapshots streams, and delegates
/// `daemon start -d` to the injected mutation-lane runner on request. See app-shell design spec §5.
public sealed class DaemonClientService : IDaemonClientService, IAsyncDisposable {
    readonly Func<CancellationToken, IAsyncEnumerable<LocalControlEvent>> _runClient;
    // The lane's RunAsync, pre-bound to a DetachedStart request for THIS daemon (or an honest
    // Refused("no_server_configured") when no canonical server can be bound) — this service no
    // longer spawns `daemon start -d` itself and carries no bare-"kcap" fallback of its own.
    readonly Func<CancellationToken, Task<MutationOutcome>> _startDaemon;

    readonly BehaviorSubject<AttachStatus> _status = new(new(AttachState.Connecting, null, null));
    readonly ReplaySubject<DaemonStatusDto> _snapshots = new(1);

    // Single-flight restart: at most one PumpAsync enumeration is ever live, so the subjects
    // and Agents cache can never observe interleaved publishers from two incarnations.
    readonly SemaphoreSlim _restartGate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _loopCts;
    Task _loop = Task.CompletedTask;

    public DaemonClientService(
            string                                                       daemonName,
            Func<CancellationToken, IAsyncEnumerable<LocalControlEvent>> runClient,
            Func<CancellationToken, Task<MutationOutcome>>               startDaemon
        ) {
        DaemonName   = daemonName;
        _runClient   = runClient;
        _startDaemon = startDaemon;
    }

    public string DaemonName { get; }

    public IObservable<AttachStatus> Status => _status.AsObservable();

    public IObservable<DaemonStatusDto> Snapshots => _snapshots.AsObservable();

    public SourceCache<AgentStatusDto, string> Agents { get; } = new(a => a.Id);

    /// Begins the attach loop with the service-lifetime token. Fire-and-forget: Start() itself
    /// never blocks the caller (Avalonia startup) on the first attach cycle.
    public void Start() => _ = RestartLoopAsync();

    // Maps one LocalControlEvent to the service's published state. On Connected, the carried
    // snapshot is applied to Snapshots/Agents BEFORE AttachStatus flips to Connected (no-stale
    // pin, spec §5) — a consumer that gates rendering on Connected can never observe it
    // alongside a previous incarnation's data.
    void Apply(LocalControlEvent e) {
        switch (e) {
            case LocalControlEvent.Connecting:
                _status.OnNext(new(AttachState.Connecting, null, null));
                break;
            case LocalControlEvent.Connected(var caps, var first, var identity):
                _snapshots.OnNext(first);
                Agents.EditDiff(first.Agents, EqualityComparer<AgentStatusDto>.Default);
                _status.OnNext(new(AttachState.Connected, null, caps, null, identity));
                break;
            case LocalControlEvent.Status(var snap):
                _snapshots.OnNext(snap);
                Agents.EditDiff(snap.Agents, EqualityComparer<AgentStatusDto>.Default);
                break;
            case LocalControlEvent.Unreachable(var reason, var version):
                _status.OnNext(new(AttachState.Unreachable, reason, null, version));
                break;
            default:
                throw new UnreachableException($"unhandled event {e.GetType().Name}");
        }
    }

    public async Task RestartLoopAsync() {
        if (_lifetime.IsCancellationRequested) return; // no-op after shutdown

        await _restartGate.WaitAsync().ConfigureAwait(false);
        try {
            if (_lifetime.IsCancellationRequested) return; // shutdown won the race for the gate

            _loopCts?.Cancel();
            await AwaitLoopQuietly(_loop); // AWAIT the old enumeration's end before starting a new one
            // Cancel does NOT unregister a linked source's registration from its parent
            // (_lifetime.Token) — only Dispose does. Without this, every restart/start-kick
            // over the app's lifetime leaks one registration on _lifetime.Token.
            _loopCts?.Dispose();

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _loop    = Task.Run(() => PumpAsync(_loopCts.Token));
        } finally {
            _restartGate.Release();
        }
    }

    async Task PumpAsync(CancellationToken ct) {
        try {
            await foreach (var e in _runClient(ct)) Apply(e);
        } catch (OperationCanceledException) {
            // Expected on restart/shutdown — the enumeration simply ends, no fabricated event.
        } catch (Exception) {
            // Contain all faults: a faulted `_loop` would wedge every later RestartLoopAsync/
            // DisposeAsync at their `await _loop`.
        }
    }

    // Defense-in-depth alongside PumpAsync's catch-all: even if a future edit reintroduces a
    // path where `_loop` faults, a bricked loop must never block RestartLoopAsync or
    // DisposeAsync from progressing.
    static async Task AwaitLoopQuietly(Task loop) {
        try { await loop.ConfigureAwait(false); }
        catch { /* contained — see PumpAsync */ }
    }

    /// Delegates to the injected lane runner and maps its MutationOutcome onto the honest
    /// StartDaemonResult the UI already understands — ct abandons the WAIT only (a detached start
    /// already spawned keeps running, never reported as a failure); the lane's own RunAsync
    /// rethrows OperationCanceledException the same way, so it just propagates here unmodified.
    /// The reattach kick fires UNCONDITIONALLY, not just on Succeeded/SucceededAfterTimeout: any
    /// mutation attempt may have restarted the daemon even when it did not end in success, and
    /// kicking reattach is idempotent — the attach doesn't sit out a backoff either way.
    public async Task<StartDaemonResult> StartDaemonAsync(CancellationToken ct) {
        var outcome = await _startDaemon(ct).ConfigureAwait(false);

        _ = RestartLoopAsync();

        return ToResult(outcome);
    }

    static StartDaemonResult ToResult(MutationOutcome outcome) => outcome switch {
        MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout => new(true, null),
        MutationOutcome.Refused("cli_not_found", _)                       => new(false, "kcap CLI not found"),
        MutationOutcome.Refused(var reason, _)                            => new(false, reason),
        MutationOutcome.Failed(var exitCode, var reason, _) =>
            new(false, reason ?? $"kcap daemon start exited with code {exitCode}"),
        MutationOutcome.AttentionSkew(var detail)   => new(false, detail),
        MutationOutcome.AttentionRepair(var detail) => new(false, detail),
        MutationOutcome.UnconfirmedNoAttach         => new(false, "daemon start not yet confirmed — check status"),
        _                                            => new(false, outcome.GetType().Name),
    };

    /// <summary>
    /// Resolves the daemon name ONCE via the same chain DaemonCommands.ResolveName uses, so the
    /// watched daemon and the started daemon can never diverge (spec §5). `runMutation` is the
    /// app-lifetime DaemonMutationLane's RunAsync, injected by the composition root so this
    /// factory never spawns a process of its own. Built over the profile the caller ALREADY
    /// resolved: the app resolves once per graph build (evaluating the onboarding gate) and builds
    /// from that same resolution, so the gate verdict and the daemon identity can never diverge on
    /// a concurrently-changing profile.
    /// </summary>
    public static DaemonClientService CreateResolved(DaemonStore store, Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation) {
        var name = DaemonNameResolver.Resolve([], AppConfig.ResolvedProfile?.Profile?.Daemon?.Name);

        return new DaemonClientService(
            name,
            ct => new LocalControlClient(store, name).RunAsync(ct),
            BuildStartDaemon(name, () => AppConfig.ResolvedProfile, runMutation)
        );
    }

    /// The main-window Start/Retry delegate: builds a DetachedStart MutationRequest at the
    /// CURRENTLY resolved profile/server (re-read on every call, never captured once — a profile
    /// resolved after this service was constructed must still be honored) and hands it to
    /// `runMutation`; a caller that cannot bind a canonical server never reaches it (binding
    /// ruling 1). Extracted from CreateResolved — whose daemon-name resolution reads real config —
    /// so this request-building logic stays unit-testable on its own.
    internal static Func<CancellationToken, Task<MutationOutcome>> BuildStartDaemon(
            string daemonName, Func<ResolvedProfile?> resolveProfile,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation) =>
        ct => {
            var profile = resolveProfile();
            var refusal = MutationRequestFactory.TryBuild(
                MutationVerb.DetachedStart, profile?.ProfileName, profile?.ServerUrl, daemonName, out var request);
            return refusal is not null ? Task.FromResult(refusal) : runMutation(request!, ct);
        };

    public async ValueTask DisposeAsync() {
        _lifetime.Cancel();

        await _restartGate.WaitAsync().ConfigureAwait(false);
        try {
            _loopCts?.Cancel();
            await AwaitLoopQuietly(_loop);
            _loopCts?.Dispose(); // the final one — RestartLoopAsync only disposes the PREVIOUS CTS on each restart
            _loopCts = null;     // DisposeAsync must stay idempotent: a second call's `_loopCts?.Cancel()`
                                 // above would otherwise throw ObjectDisposedException instead of no-op'ing
        } finally {
            _restartGate.Release();
        }

        _status.Dispose();
        _snapshots.Dispose();
        Agents.Dispose();
    }

    /// Production IProcessRunner: wraps System.Diagnostics.Process with stdout/stderr capture, an
    /// env overlay, an internal timeout, and a per-call cancel mode. `RunOptions.Timeout` is an
    /// internal deadline distinct from `ct`: on expiry the process (or tree, per `RunOptions.TimeoutKill`) is killed and awaited, and the
    /// result comes back with TimedOut=true rather than throwing. `ct` cancellation behaves per
    /// `RunOptions.CancelMode`: AbandonWait abandons the WAIT only (a detached `daemon start -d`
    /// keeps running) and still throws OperationCanceledException; KillTree kills the tree and
    /// awaits its exit first, then STILL throws — cancellation is cancellation, TimedOut is only
    /// for the internal Timeout.
    /// Internal (not private): lets ProcessRunnerTests drive a REAL child process, since
    /// IProcessRunner itself is only a seam for DaemonClientService's own consumers.
    internal sealed class ProcessRunner : IProcessRunner {
        public async Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            var psi = new ProcessStartInfo(fileName) {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (options.EnvOverlay is not null)
                foreach (var (key, value) in options.EnvOverlay) psi.Environment[key] = value;

            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
            // CancellationToken.None on both drains: neither `ct` nor the internal timeout ever
            // abandons the pipes — a drain tied to either would stop reading on cancellation and
            // let a detached/killed child block on a full pipe buffer.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutCts = options.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
            using var waitCts = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try {
                await process.WaitForExitAsync(waitCts?.Token ?? ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                if (ct.IsCancellationRequested) {
                    if (options.CancelMode == CancelMode.KillTree)
                        await KillAndAwaitAsync(process).ConfigureAwait(false);

                    // The drains outlive this method on the abandoned-wait path — observe them
                    // so a later fault surfaces nowhere instead of as an unobserved task
                    // exception. Under KillTree the child is already dead, so this still
                    // completes promptly; it just isn't awaited before the throw below.
                    Observe(stdoutTask);
                    Observe(stderrTask);
                    throw;
                }

                // Only the internal timeout could have fired the linked token.
                await KillAndAwaitAsync(process, options.TimeoutKill == TimeoutKillScope.Tree).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: true);
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false);
        }

        const int TailLimit = 500;

        public async Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                Action<StreamedLine> onLine, CancellationToken ct) {
            var psi = new ProcessStartInfo(fileName) {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (options.EnvOverlay is not null)
                foreach (var (key, value) in options.EnvOverlay) psi.Environment[key] = value;

            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

            var tailLock = new object();
            var tail = new Queue<StreamedLine>(TailLimit + 1);

            void Record(StreamedLine line) {
                try { onLine(line); }
                catch (Exception ex) { Console.Error.WriteLine($"kcap: streaming callback threw for '{fileName}': {ex}"); }

                lock (tailLock) {
                    tail.Enqueue(line);
                    if (tail.Count > TailLimit) tail.Dequeue();
                }
            }

            // Line-buffered per stream — no cross-stream ordering promise; drains to EOF even under kill.
            async Task PumpAsync(TextReader reader, ProcessStreamKind kind) {
                string? line;
                while ((line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
                    Record(new StreamedLine(kind, line));
            }

            var stdoutTask = PumpAsync(process.StandardOutput, ProcessStreamKind.Stdout);
            var stderrTask = PumpAsync(process.StandardError, ProcessStreamKind.Stderr);

            using var timeoutCts = options.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
            using var waitCts = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try {
                await process.WaitForExitAsync(waitCts?.Token ?? ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                if (ct.IsCancellationRequested) {
                    // Streaming always kills the tree on cancellation, ignoring RunOptions.CancelMode.
                    await KillAndAwaitAsync(process).ConfigureAwait(false);
                    // Awaited, not fire-and-forget: the pumps end at EOF once the tree is killed
                    // (same as the timeout arm below) — a fire-and-forget Observe let a callback
                    // fire AFTER this method had already thrown OCE, racing caller cleanup.
                    await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                    throw;
                }

                await KillAndAwaitAsync(process, options.TimeoutKill == TimeoutKillScope.Tree).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                lock (tailLock) return new StreamingResult(process.ExitCode, TimedOut: true, tail.ToArray());
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            lock (tailLock) return new StreamingResult(process.ExitCode, TimedOut: false, tail.ToArray());
        }

        static async Task KillAndAwaitAsync(Process process, bool entireProcessTree = true) {
            try { process.Kill(entireProcessTree); }
            catch (InvalidOperationException) { /* already exited */ }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
