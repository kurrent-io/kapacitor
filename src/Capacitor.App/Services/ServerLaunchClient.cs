using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.App.Services;

/// Opens the hub connection lazily on first launch and reuses it. URL suffix and access-token
/// provider mirror the daemon's own ServerConnection (same server, same auth) so the two never
/// drift onto separate conventions.
public sealed class ServerLaunchClient : ILaunchClient, IAsyncDisposable {
    readonly SemaphoreSlim _gate = new(1, 1);
    HubConnection? _hub;

    public async Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
        HubConnection hub;
        try {
            hub = await GetConnectionAsync(ct);
        } catch (Exception ex) {
            return new LaunchOutcome(false, null, ex.Message);
        }

        try {
            var agentId = await hub.InvokeAsync<string>("RequestLaunchAgentV2", LaunchPayload.For(request), ct);
            return new LaunchOutcome(Started: true, AgentId: agentId, Error: null);
        } catch (Exception ex) {
            // HubException carries the server's own rejection text (capacity, unknown vendor,
            // consent denial) — that IS the message Home should show, not a generic wrapper.
            return new LaunchOutcome(false, null, ex.Message);
        }
    }

    async Task<HubConnection> GetConnectionAsync(CancellationToken ct) {
        await _gate.WaitAsync(ct);
        try {
            if (_hub is { State: HubConnectionState.Connected }) return _hub;

            if (_hub is not null) {
                await _hub.DisposeAsync();
                _hub = null;
            }

            var serverUrl = AppConfig.ResolvedServerUrl
                ?? throw new InvalidOperationException("No server configured — run kcap setup first.");

            var hub = new HubConnectionBuilder()
                .WithUrl(
                    $"{serverUrl.TrimEnd('/')}/hubs/sessions",
                    options => {
                        options.AccessTokenProvider = async () => {
                            var resolution = await TokenStore.GetValidTokensForServerAsync(serverUrl);
                            return resolution.Tokens?.AccessToken;
                        };
                    }
                )
                // AOT: point the JSON protocol at the generated context, or the payload
                // serializes reflectively at runtime and NativeAOT publish warns. Shared with
                // the tests via LaunchHubJson so the wire format can't silently diverge.
                .AddJsonProtocol(o => LaunchHubJson.Configure(o.PayloadSerializerOptions))
                .Build();

            // A failed start leaves a live HubConnection nothing else can reach (_hub is still
            // null), so it must be disposed here or its transport/timers leak for the run.
            try {
                await hub.StartAsync(ct);
            } catch {
                await hub.DisposeAsync();
                throw;
            }

            _hub = hub;
            return hub;
        } finally {
            _gate.Release();
        }
    }

    /// Takes the gate first: disposing it out from under an in-flight GetConnectionAsync would
    /// fault that launch's Release. A launch arriving after this point fails with
    /// ObjectDisposedException, which StartAsync already turns into a failed LaunchOutcome.
    public async ValueTask DisposeAsync() {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            if (_hub is not null) {
                await _hub.DisposeAsync();
                _hub = null;
            }
        } finally {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
