using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

[NotInParallel(nameof(HubTestHost))]
public class ServerConnectionServiceTests {
    static ServerConnectionService Lane(HubTestHost host, string? token = null) =>
        new(host.Url, () => Task.FromResult(token));

    static async Task<T> Next<T>(IObservable<T> source, Func<T, bool> match, int seconds = 10) =>
        await source.Where(match).Take(1).ToTask().WaitAsync(TimeSpan.FromSeconds(seconds));

    [Test]
    public async Task ConnectsAndServesDaemons() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.DaemonsHandler = () => [new DaemonInfo { Name = "work-mac", Connected = true }];
        await using var lane = Lane(host);
        lane.Start();

        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        var daemons = await lane.GetConnectedDaemonsAsync(CancellationToken.None);
        await Assert.That(daemons![0].Name).IsEqualTo("work-mac");
    }

    [Test]
    public async Task BroadcastsSurfaceAsObservables() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        var agentsPing = lane.AgentInstancesChanged.Take(1).ToTask();
        var failure    = lane.LaunchFailures.Take(1).ToTask();
        await host.BroadcastAsync(HubBroadcasts.AgentInstancesChanged);
        await host.BroadcastAsync(HubBroadcasts.LaunchFailed, "a1", "launch_denied_by_owner: default");
        await agentsPing.WaitAsync(TimeSpan.FromSeconds(10));
        var f = await failure.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(f.AgentId).IsEqualTo("a1");
        await Assert.That(f.Reason).Contains("launch_denied_by_owner");
    }

    [Test]
    public async Task NoServerMeansDormantForever() {
        await using var lane = new ServerConnectionService(serverUrl: null, () => Task.FromResult<string?>(null));
        lane.Start();
        var status = await lane.Status.Take(1).ToTask();
        await Assert.That(status.State).IsEqualTo(ServerLaneState.Dormant);
        await Assert.That(await lane.GetConnectedDaemonsAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ColdStartFailureRetriesUntilServerAppears() {
        // Pins only: a cold connect against a dead URL (a started-then-stopped host, so the URL
        // is real but nothing answers) settles on Retrying, never throwing out of RunAsync.
        await using var host = await HubTestHost.StartAsync();
        var url = host.Url;
        await host.StopAsync();

        await using var lane = new ServerConnectionService(url, () => Task.FromResult<string?>(null));
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Retrying, seconds: 15);
    }

    [Test]
    public async Task MissingTeamClaimSetsDiagnostic() {
        await using var host = await HubTestHost.StartAsync();
        // "sub" only — no team_id. Header/payload/sig shape per JwtClaimsTests.
        const string token = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ1MSJ9.s";
        await using var lane = Lane(host, token);
        lane.Start();
        var status = await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        await Assert.That(status.Diagnostic).IsEqualTo(ServerConnectionService.TeamClaimMissingNotice);
    }

    [Test]
    public async Task ConnectedThenServerClosesSurfacesRetrying() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        await host.StopAsync();
        await Next(lane.Status, s => s.State == ServerLaneState.Retrying, seconds: 15);
    }

    [Test]
    public async Task RestartReconnects() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        await lane.RestartAsync();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
    }

    [Test]
    public async Task LaunchInvokesOverTheSharedConnection() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.LaunchHandler = payload => {
            // The payload arrives with the pinned snake_case names whatever the policy does.
            if (!payload.TryGetProperty("daemon_name", out var d) || d.GetString() != "work-mac")
                throw new InvalidOperationException("daemon_name missing");
            return "agent-42";
        };
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        var outcome = await ((ILaunchClient)lane).StartAsync(
            new LaunchRequest("work-mac", "/work/repo", "claude", "do it"), CancellationToken.None);
        await Assert.That(outcome.Started).IsTrue();
        await Assert.That(outcome.AgentId).IsEqualTo("agent-42");
        await Assert.That(HubTestHost.LaunchCalls).IsEqualTo(1);
    }

    [Test]
    public async Task LaunchWhileDisconnectedFailsWithoutThrowing() {
        await using var lane = new ServerConnectionService("http://127.0.0.1:1", () => Task.FromResult<string?>(null));
        lane.Start();
        var outcome = await ((ILaunchClient)lane).StartAsync(
            new LaunchRequest("d", "/r", "claude", null), CancellationToken.None);
        await Assert.That(outcome.Started).IsFalse();
        await Assert.That(outcome.Error).IsNotNull();
    }

    [Test]
    public async Task UnauthorizedNegotiateSurfacesSignedOutAndStaysThere() {
        await using var host = await HubTestHost.StartAsync(requireAuth: true);
        string? token = null;
        await using var lane = new ServerConnectionService(host.Url, () => Task.FromResult(token));
        lane.Start();

        await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);

        // RunAsync's loop exits (never schedules a retry) on SignedOut, so the current value is
        // the final one until RestartAsync runs — nothing further to race against here.
        var latest = await lane.Status.Take(1).ToTask();
        await Assert.That(latest.State).IsEqualTo(ServerLaneState.SignedOut);

        var outcome = await ((ILaunchClient)lane).StartAsync(
            new LaunchRequest("d", "/r", "claude", null), CancellationToken.None);
        await Assert.That(outcome.Started).IsFalse();
        await Assert.That(outcome.Unauthorized).IsTrue();

        token = "some-token";
        await lane.RestartAsync();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
    }

    [Test]
    public async Task ParkSignedOutPublishesSignedOutAndStopsTheLoopUntilRestarted() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        lane.ParkSignedOut();
        await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);

        var outcome = await ((ILaunchClient)lane).StartAsync(
            new LaunchRequest("d", "/r", "claude", null), CancellationToken.None);
        await Assert.That(outcome.Started).IsFalse();
        await Assert.That(outcome.Unauthorized).IsTrue();

        await lane.RestartAsync();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
    }

    [Test]
    public async Task ParkSignedOutWithIfEpochIgnoresAStaleEpochButHonorsTheCurrentOne() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        var connected = await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        var staleEpoch = connected.Epoch;

        lane.ParkSignedOut();
        var parked = await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);
        var currentEpoch = parked.Epoch;

        await lane.RestartAsync();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected); // "a fresh Connected"

        lane.ParkSignedOut(staleEpoch); // captured before the park above — must be ignored
        await Task.Delay(200);
        var stillConnected = await lane.Status.Take(1).ToTask();
        await Assert.That(stillConnected.State).IsEqualTo(ServerLaneState.Connected);

        lane.ParkSignedOut(currentEpoch); // the epoch the reconnect never changed — must still park
        await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);
    }

    [Test]
    public async Task ParkSignedOutDuringATransportLossRetryingSequenceStaysParked() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        await host.StopAsync(); // triggers SignalR's own automatic-reconnect cycle
        await Next(lane.Status, s => s.State == ServerLaneState.Retrying, seconds: 15);

        lane.ParkSignedOut();
        await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);

        // A further reconnect attempt from the SAME (now epoch-stale) loop, if the guard failed,
        // would overwrite this — give the retry policy's next tick a chance to (wrongly) land.
        await Task.Delay(4000);
        var latest = await lane.Status.Take(1).ToTask();
        await Assert.That(latest.State).IsEqualTo(ServerLaneState.SignedOut);
    }

    // Each connect attempt's token provider calls are: SignalR's own two internal reads
    // (negotiate + transport, resolved fast so hub.StartAsync completes normally), then
    // DiagnoseAsync's own third read, gated so the park below deterministically lands while that
    // continuation is still pending. The per-attempt counter resets on every Connecting so a
    // retry (should one happen under load) re-fast-paths its own first two reads rather than
    // inheriting a stale count from an earlier attempt.
    [Test]
    public async Task ParkSignedOutDuringAnInFlightConnectDiscardsTheRacingConnectedPublish() {
        var attemptCallIndex = 0;
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await HubTestHost.StartAsync();
        await using var lane = new ServerConnectionService(host.Url, () => {
            var n = Interlocked.Increment(ref attemptCallIndex);
            return n <= 2 ? Task.FromResult<string?>(null) : gate.Task;
        });
        using var attemptReset = lane.Status
            .Where(s => s.State == ServerLaneState.Connecting)
            .Subscribe(_ => Interlocked.Exchange(ref attemptCallIndex, 0));
        lane.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref attemptCallIndex) < 3) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("DiagnoseAsync's token read was never reached");
            await Task.Delay(5);
        }

        lane.ParkSignedOut();
        gate.SetResult(null); // release DiagnoseAsync — its Connected publish must be discarded

        await Task.Delay(200); // give the racing Connected publish, if any, time to (wrongly) land
        var latest = await lane.Status.Take(1).ToTask();
        await Assert.That(latest.State).IsEqualTo(ServerLaneState.SignedOut);
    }

    [Test]
    public async Task UnauthorizedIsDetectedAnywhereInTheExceptionChain() {
        var wrapped = new InvalidOperationException(
            "outer", new HttpRequestException("401", null, System.Net.HttpStatusCode.Unauthorized));

        await Assert.That(ServerConnectionService.IsUnauthorized(wrapped)).IsTrue();
        await Assert.That(ServerConnectionService.IsUnauthorized(
            new HttpRequestException("403", null, System.Net.HttpStatusCode.Forbidden))).IsFalse();
        await Assert.That(ServerConnectionService.IsUnauthorized(new InvalidOperationException("no http"))).IsFalse();
    }
}
