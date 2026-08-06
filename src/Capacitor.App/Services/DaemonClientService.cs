using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Services;

/// Rx/DynamicData adapter over LocalControlClient.RunAsync: owns the single live attach
/// enumeration, publishes the atomic AttachStatus + Snapshots streams and the keyed Agents
/// cache, and spawns `kcap daemon start` on request. See app-shell design spec §5.
public sealed class DaemonClientService : IDaemonClientService, IAsyncDisposable {
    readonly Func<CancellationToken, IAsyncEnumerable<LocalControlEvent>> _runClient;
    readonly IProcessRunner _processRunner;
    readonly string _cliPath;

    readonly BehaviorSubject<AttachStatus> _status = new(new(AttachState.Connecting, null, null));
    readonly ReplaySubject<DaemonStatusDto> _snapshots = new(1);

    // Single-flight restart: at most one PumpAsync enumeration is ever live, so the subjects
    // and Agents cache can never observe interleaved publishers from two incarnations.
    readonly SemaphoreSlim _restartGate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _loopCts;
    Task _loop = Task.CompletedTask;

    public DaemonClientService(
            string daemonName,
            Func<CancellationToken, IAsyncEnumerable<LocalControlEvent>> runClient,
            IProcessRunner processRunner,
            string cliPath) {
        DaemonName    = daemonName;
        _runClient    = runClient;
        _processRunner = processRunner;
        _cliPath      = cliPath;
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
            case LocalControlEvent.Connected(var caps, var first):
                _snapshots.OnNext(first);
                Agents.EditDiff(first.Agents, EqualityComparer<AgentStatusDto>.Default);
                _status.OnNext(new(AttachState.Connected, null, caps));
                break;
            case LocalControlEvent.Status(var snap):
                _snapshots.OnNext(snap);
                Agents.EditDiff(snap.Agents, EqualityComparer<AgentStatusDto>.Default);
                break;
            case LocalControlEvent.Unreachable(var reason):
                _status.OnNext(new(AttachState.Unreachable, reason, null));
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

    public async Task<StartDaemonResult> StartDaemonAsync(CancellationToken ct) {
        try {
            var (exit, stderr) = await _processRunner
                .RunAsync(_cliPath, ["daemon", "start", "-d", "--name", DaemonName], ct)
                .ConfigureAwait(false);

            if (exit == 0) {
                _ = RestartLoopAsync(); // immediate kick — the attach doesn't sit out a backoff
                return new(true, null);
            }

            return new(false, string.IsNullOrWhiteSpace(stderr)
                ? $"kcap daemon start exited with code {exit}"
                : stderr.Trim());
        } catch (OperationCanceledException) {
            throw; // ct abandons the WAIT, not the started daemon — never reported as a failure
        } catch (Exception ex) {
            return new(false, ex.Message);
        }
    }

    /// Resolves the daemon name ONCE via the same chain DaemonCommands.ResolveName uses, so the
    /// watched daemon and the started daemon can never diverge (spec §5).
    public static async Task<DaemonClientService> CreateDefaultAsync() {
        await AppConfig.ResolveActiveProfile([]);
        var name = DaemonNameResolver.Resolve([], AppConfig.ResolvedProfile?.Profile?.Daemon?.Name);

        var cliPath = Environment.GetEnvironmentVariable("KCAP_APP_CLI_PATH") is { Length: > 0 } overridePath
            ? overridePath
            : "kcap";

        return new DaemonClientService(name, ct => new LocalControlClient(name).RunAsync(ct), new ProcessRunner(), cliPath);
    }

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

    /// Production IProcessRunner: wraps System.Diagnostics.Process with stderr capture and a
    /// ct-linked wait. `ct` abandons the WAIT only — a detached `daemon start -d` keeps running.
    /// Internal (not private): lets ProcessRunnerTests drive a REAL child process, since
    /// IProcessRunner itself is only a seam for DaemonClientService's own consumers.
    internal sealed class ProcessRunner : IProcessRunner {
        public async Task<(int ExitCode, string Stderr)> RunAsync(string fileName, string[] args, CancellationToken ct) {
            var psi = new ProcessStartInfo(fileName) {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
            // CancellationToken.None on both drains: `ct` abandons the WAIT below (class doc),
            // never the pipes — a drain tied to `ct` would stop reading on cancellation and let
            // a detached child block on a full pipe buffer.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // The drains outlive this method on the abandoned-wait path — observe them so a
                // later fault surfaces nowhere instead of as an unobserved task exception.
                Observe(stdoutTask);
                Observe(stderrTask);
                throw;
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return (process.ExitCode, stderrTask.Result);
        }

        static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
