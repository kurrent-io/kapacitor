using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Remote.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.App.Services;

/// The app's one long-lived server connection. Handlers are registered before StartAsync so no
/// broadcast can slip past a fresh connection; a closed or cold-failed connection re-dials on a
/// 1/2/5/10/30s ladder because SignalR's automatic reconnect covers neither cold-start failure
/// nor a close it decides not to retry.
public sealed class ServerConnectionService : IServerLane, ILaunchClient, IAsyncDisposable {
    public const string TeamClaimMissingNotice =
        "Signed-in token carries no team claim — server broadcasts may not reach this app.";

    static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    readonly string? _serverUrl;
    readonly Func<Task<string?>> _token;
    readonly BehaviorSubject<ServerLaneStatus> _status = new(new(ServerLaneState.Dormant));
    readonly Subject<Unit> _agentsChanged = new();
    readonly Subject<Unit> _daemonsChanged = new();
    readonly Subject<LaunchFailure> _launchFailures = new();
    readonly SemaphoreSlim _restartGate = new(1, 1);
    // One lock owns the whole lane lifecycle: the generation, the current loop's cancellation
    // source, and EVERY status publish. A generation names one admitted connect loop; a park, a
    // restart and teardown each advance it, so a publish carrying an older generation belongs to a
    // loop that has been superseded and is dropped rather than overwriting whatever superseded it.
    readonly Lock _lifecycleLock = new();
    int _generation;
    // The admitted loop's own source, so a park cancels the loop that was running when it decided
    // to park — never one a concurrent restart has since put in its place. Null between a restart
    // retiring the old loop and admitting the new one.
    CancellationTokenSource? _loopCts;
    // Set under the lock before the subjects are disposed, so no publish can be in flight when
    // they go.
    bool _closed;
    readonly CancellationTokenSource _lifetime = new();
    Task _loop = Task.CompletedTask;
    volatile HubConnection? _hub;

    public ServerConnectionService(ConfigRoot config, ProfileContext? profiles)
        : this(
            profiles?.Resolution.ServerUrl,
            profiles is null
                ? () => Task.FromResult<string?>(null)
                : async () => (await new TokenStore(config).GetValidTokensForServerAsync(
                    profiles.Name, profiles.Resolution.ServerUrl!)).Tokens?.AccessToken) { }

    internal ServerConnectionService(string? serverUrl, Func<Task<string?>> accessTokenProvider) {
        _serverUrl = string.IsNullOrEmpty(serverUrl) ? null : serverUrl.TrimEnd('/');
        _token = accessTokenProvider;
    }

    public IObservable<ServerLaneStatus> Status => _status.AsObservable();
    public IObservable<Unit> AgentInstancesChanged => _agentsChanged.AsObservable();
    public IObservable<Unit> DaemonsChanged => _daemonsChanged.AsObservable();
    public IObservable<LaunchFailure> LaunchFailures => _launchFailures.AsObservable();

    public void Start() {
        if (_serverUrl is null) return;
        _ = RestartAsync();
    }

    /// Same parked semantics as the 401-negotiate path in RunAsync (SignedOut, loop stopped) but
    /// triggered from outside it — RemoteAgentsService's onUnauthorized, when its own HTTP fetch
    /// hits a 401 the hub connection itself never saw. RestartAsync (wired to sign-in completion)
    /// is what revives it.
    public void ParkSignedOut() => ParkSignedOutCore(ifGeneration: null, detail: null);

    /// Parks only while `ifEpoch` is still the lane's current generation — the generation of the
    /// Connected status the caller's own decision was made under. RemoteAgentsService releases its
    /// cache lock before invoking onUnauthorized (taking it around the callback would invert it
    /// against this lane's lock), so by the time this runs a park, a restart or a teardown may have
    /// moved the lane on; re-validating here, atomically with the park itself, is what that
    /// callback could not do for itself.
    public void ParkSignedOut(int ifEpoch) => ParkSignedOutCore(ifEpoch, detail: null);

    void ParkSignedOutCore(int? ifGeneration, string? detail) {
        lock (_lifecycleLock) {
            if (_closed) return;
            if (ifGeneration is { } generation && generation != _generation) return;
            // Advancing first is what makes the park terminal: every publish the parked loop still
            // has in flight — its Connecting, its Retrying, a Connected waiting on DiagnoseAsync —
            // now carries a stale generation and is dropped. Cancelling the captured source under
            // the same lock stops the loop admitted at THIS generation and no other.
            _generation++;
            _status.OnNext(new(ServerLaneState.SignedOut, detail, Epoch: _generation));
            _loopCts?.Cancel();
        }
    }

    /// Retires the running loop and admits a new one under a fresh generation. A park landing while
    /// the old loop is still being awaited wins: the generation this restart reserved is no longer
    /// current, so no loop is admitted and the lane stays parked until the next restart.
    public async Task RestartAsync(CancellationToken ct = default) {
        if (_serverUrl is null || _lifetime.IsCancellationRequested) return;
        await _restartGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_lifetime.IsCancellationRequested) return;
            CancellationTokenSource? previous;
            int generation;
            lock (_lifecycleLock) {
                if (_closed) return;
                previous = _loopCts;
                // Unreachable to a park from here, so disposing it below cannot race one.
                _loopCts = null;
                _generation++;
                generation = _generation;
            }
            previous?.Cancel();
            await AwaitQuietly(_loop).ConfigureAwait(false);
            previous?.Dispose();

            CancellationToken token;
            lock (_lifecycleLock) {
                if (_closed || generation != _generation) return;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _loopCts = cts;
                token = cts.Token;
            }
            _loop = Task.Run(() => RunAsync(generation, token), CancellationToken.None);
        } finally {
            _restartGate.Release();
        }
    }

    async Task RunAsync(int generation, CancellationToken ct) {
        var attempt = 0;
        while (!ct.IsCancellationRequested) {
            // A refused publish means this loop has been superseded — parked, restarted or torn
            // down — and has nothing left to dial for.
            if (!Publish(generation, new(ServerLaneState.Connecting))) return;
            HubConnection? hub = null;
            try {
                hub = Build();
                var capturedHub = hub;

                // Registered before StartAsync (SignalR supports that), so a close during the
                // DiagnoseAsync token read below — or during StartAsync itself — is still
                // observed, never lost with status stuck on a dead hub.
                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                hub.Closed += ex => { closed.TrySetResult(ex); return Task.CompletedTask;  };
                hub.Reconnecting += _ => {
                    Publish(generation, new(ServerLaneState.Retrying, "reconnecting"));
                    return Task.CompletedTask;
                };
                hub.Reconnected += async _ => {
                    var (diagnostic, subject) = await DiagnoseAsync().ConfigureAwait(false);
                    PublishConnected(generation, capturedHub, diagnostic, subject);
                };

                await hub.StartAsync(ct).ConfigureAwait(false);
                _hub = hub;
                attempt = 0;
                var (connectedDiagnostic, connectedSubject) = await DiagnoseAsync().ConfigureAwait(false);
                PublishConnected(generation, capturedHub, connectedDiagnostic, connectedSubject);

                Exception? closeReason;
                await using (ct.Register(() => closed.TrySetResult(null)))
                    closeReason = await closed.Task.ConfigureAwait(false);

                if (!ct.IsCancellationRequested)
                    Publish(generation, new(ServerLaneState.Retrying, closeReason?.Message));
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) when (IsUnauthorized(ex)) {
                // The credential is what kcap login repairs, not this loop — retrying a 401
                // negotiate forever would just burn the backoff ladder for no reason. The lane
                // parks here until RestartAsync (wired to sign-in completion) admits a fresh loop,
                // through the same park a delayed unauthorized fetch takes.
                ParkSignedOutCore(generation, ex.Message);
                return;
            } catch (Exception ex) {
                Publish(generation, new(ServerLaneState.Retrying, ex.Message));
            } finally {
                _hub = null;
                if (hub is not null) await hub.DisposeAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) break;
            var delay = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    /// The one place a lifecycle status reaches subscribers: it carries the generation that
    /// produced it, and lands only while that generation is still current. False means the caller
    /// has been superseded.
    bool Publish(int generation, ServerLaneStatus status) {
        lock (_lifecycleLock) {
            if (_closed || generation != _generation) return false;
            _status.OnNext(status with { Epoch = generation });
            return true;
        }
    }

    HubConnection Build() {
        var hub = new HubConnectionBuilder()
            .WithUrl($"{_serverUrl}/hubs/sessions", o => o.AccessTokenProvider = _token)
            .WithAutomaticReconnect()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
            .Build();
        hub.On(HubBroadcasts.AgentInstancesChanged, () => _agentsChanged.OnNext(Unit.Default));
        hub.On(HubBroadcasts.DaemonsChanged, () => _daemonsChanged.OnNext(Unit.Default));
        hub.On<string, string>(HubBroadcasts.LaunchFailed, (agentId, reason) => _launchFailures.OnNext(new(agentId, reason)));
        return hub;
    }

    /// Connected additionally requires `hub` to still be the live connection and still be connected
    /// at publish time: DiagnoseAsync's await can outlast either, and a Connected published over a
    /// dead hub reaches downstream DistinctUntilChanged and swallows the real transition that
    /// followed it. Folded into the same locked check as the generation, so neither the Reconnecting
    /// handler nor a park can interleave with it.
    void PublishConnected(int generation, HubConnection hub, string? diagnostic, string? subject) {
        lock (_lifecycleLock) {
            if (!ReferenceEquals(_hub, hub) || hub.State != HubConnectionState.Connected) return;
            Publish(generation, new(ServerLaneState.Connected, Diagnostic: diagnostic, Subject: subject));
        }
    }

    async Task<(string? Diagnostic, string? Subject)> DiagnoseAsync() {
        try {
            var token = await _token().ConfigureAwait(false);
            if (token is null) return (null, null);
            var diagnostic = JwtClaims.TryGetString(token, "team_id") is null ? TeamClaimMissingNotice : null;
            return (diagnostic, JwtClaims.TryGetString(token, "sub"));
        } catch {
            return (null, null);
        }
    }

    public async Task<IReadOnlyList<DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return null;
        try {
            return await hub.InvokeAsync<List<DaemonInfo>>(HubMethods.GetConnectedDaemons, ct).ConfigureAwait(false);
        } catch (Exception) {
            return null;
        }
    }

    public async Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
        if (_status.Value.State == ServerLaneState.SignedOut)
            return new LaunchOutcome(false, null, "Not signed in to the server.", Unauthorized: true);
        try {
            var hub = _hub;
            if (hub is not { State: HubConnectionState.Connected })
                return new LaunchOutcome(false, null, "Not connected to the server.");
            var agentId = await hub.InvokeAsync<string>(
                HubMethods.RequestLaunchAgentV2, LaunchPayload.For(request), ct).ConfigureAwait(false);
            return new LaunchOutcome(Started: true, AgentId: agentId, Error: null);
        } catch (Exception ex) {
            return new LaunchOutcome(false, null, ex.Message, IsUnauthorized(ex));
        }
    }

    /// Walks the chain because SignalR surfaces the negotiate failure wrapped as often as bare.
    internal static bool IsUnauthorized(Exception ex) {
        for (Exception? e = ex; e is not null; e = e.InnerException) {
            if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }) return true;
        }
        return false;
    }

    static async Task AwaitQuietly(Task t) {
        try { await t.ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync() {
        _lifetime.Cancel();
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try {
            CancellationTokenSource? loop;
            lock (_lifecycleLock) {
                _closed = true;
                _generation++;
                loop = _loopCts;
                _loopCts = null;
            }
            loop?.Cancel();
            await AwaitQuietly(_loop).ConfigureAwait(false);
            loop?.Dispose();
        } finally {
            _restartGate.Release();
        }
        _status.Dispose();
        _agentsChanged.Dispose();
        _daemonsChanged.Dispose();
        _launchFailures.Dispose();
    }
}
