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
public sealed class ServerConnectionService : IServerLane, IAsyncDisposable {
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
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _loopCts;
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

    public async Task RestartAsync(CancellationToken ct = default) {
        if (_serverUrl is null || _lifetime.IsCancellationRequested) return;
        await _restartGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_lifetime.IsCancellationRequested) return;
            _loopCts?.Cancel();
            await AwaitQuietly(_loop).ConfigureAwait(false);
            _loopCts?.Dispose();
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _loop = Task.Run(() => RunAsync(_loopCts.Token), CancellationToken.None);
        } finally {
            _restartGate.Release();
        }
    }

    async Task RunAsync(CancellationToken ct) {
        var attempt = 0;
        while (!ct.IsCancellationRequested) {
            _status.OnNext(new(ServerLaneState.Connecting));
            HubConnection? hub = null;
            try {
                hub = Build();
                await hub.StartAsync(ct).ConfigureAwait(false);
                _hub = hub;
                attempt = 0;
                _status.OnNext(new(ServerLaneState.Connected, Diagnostic: await DiagnoseAsync().ConfigureAwait(false)));

                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                hub.Closed += ex => { closed.TrySetResult(ex); return Task.CompletedTask;  };
                hub.Reconnecting += _ => { _status.OnNext(new(ServerLaneState.Retrying, "reconnecting")); return Task.CompletedTask; };
                hub.Reconnected += async _ =>
                    _status.OnNext(new(ServerLaneState.Connected, Diagnostic: await DiagnoseAsync().ConfigureAwait(false)));

                await using (ct.Register(() => closed.TrySetResult(null)))
                    await closed.Task.ConfigureAwait(false);
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                _status.OnNext(new(ServerLaneState.Retrying, ex.Message));
            } finally {
                _hub = null;
                if (hub is not null) await hub.DisposeAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) break;
            var delay = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
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

    async Task<string?> DiagnoseAsync() {
        try {
            var token = await _token().ConfigureAwait(false);
            return token is not null && JwtClaims.TryGetString(token, "team_id") is null
                ? TeamClaimMissingNotice
                : null;
        } catch {
            return null;
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

    static async Task AwaitQuietly(Task t) {
        try { await t.ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync() {
        _lifetime.Cancel();
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try {
            _loopCts?.Cancel();
            await AwaitQuietly(_loop).ConfigureAwait(false);
            _loopCts?.Dispose();
            _loopCts = null;
        } finally {
            _restartGate.Release();
        }
        _status.Dispose();
        _agentsChanged.Dispose();
        _daemonsChanged.Dispose();
        _launchFailures.Dispose();
        _restartGate.Dispose();
    }
}
