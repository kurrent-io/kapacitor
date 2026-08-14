using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// Plain TUnit tests — OneShotObservation is driven via its scripted Probe seam, LiveGraphObservation via FakeDaemonClientService.
public class DaemonObservationTests {
    static MutationRequest Req(string daemonName = "daemon-a", string server = "http://localhost:9999") =>
        new(MutationVerb.StartVerified, "default", server, daemonName);

    // --- OneShotObservation ---

    [Test]
    public async Task OneShot_reachable_consistent_maps_hello_and_snapshot_fields() {
        var hello = new HelloReplyDto(1, "1.2.3", "daemon-a", ["status/1"], Pid: 111, InstanceId: "inst-1");
        var snap = FakeDaemonClientService.Snap("daemon-a", serverUrl: "http://localhost:9999", pid: 111, instanceId: "inst-1");
        var probeResult = new ProbeResult(true, hello, snap, IdentityConsistent: true);
        var adapter = new OneShotObservation(TimeSpan.FromSeconds(1)) { Probe = (_, _, _) => Task.FromResult(probeResult) };

        var evidence = await adapter.ObserveAsync(Req(), CancellationToken.None);

        await Assert.That(evidence).IsEqualTo(new ObservedEvidence(
            true, hello.Capabilities, "1.2.3", "http://localhost:9999", "daemon-a", 111, "inst-1", true));
    }

    [Test]
    public async Task OneShot_reachable_inconsistent_carries_false() {
        var hello = new HelloReplyDto(1, "1.2.3", "daemon-a", ["status/1"], Pid: 111, InstanceId: "inst-1");
        var snap = FakeDaemonClientService.Snap("daemon-a", serverUrl: "http://localhost:9999", pid: 222, instanceId: "inst-2");
        var probeResult = new ProbeResult(true, hello, snap, IdentityConsistent: false);
        var adapter = new OneShotObservation(TimeSpan.FromSeconds(1)) { Probe = (_, _, _) => Task.FromResult(probeResult) };

        var evidence = await adapter.ObserveAsync(Req(), CancellationToken.None);

        await Assert.That(evidence!.Reachable).IsTrue();
        await Assert.That(evidence.IdentityConsistent).IsFalse();
    }

    [Test]
    public async Task OneShot_unreachable_maps_to_false_evidence() {
        var probeResult = new ProbeResult(false, null, null, false);
        var adapter = new OneShotObservation(TimeSpan.FromSeconds(1)) { Probe = (_, _, _) => Task.FromResult(probeResult) };

        var evidence = await adapter.ObserveAsync(Req(), CancellationToken.None);

        await Assert.That(evidence).IsEqualTo(new ObservedEvidence(false, null, null, null, null, null, null, false));
    }

    [Test]
    public async Task OneShot_pre_slice_daemon_null_pid_instance_is_inconsistent() {
        var hello = new HelloReplyDto(1, "1.2.3", "daemon-a", ["status/1"]); // predates Pid/InstanceId
        var snap = FakeDaemonClientService.Snap("daemon-a", serverUrl: "http://localhost:9999"); // predates Pid/InstanceId
        var probeResult = new ProbeResult(true, hello, snap, IdentityConsistent: false);
        var adapter = new OneShotObservation(TimeSpan.FromSeconds(1)) { Probe = (_, _, _) => Task.FromResult(probeResult) };

        var evidence = await adapter.ObserveAsync(Req(), CancellationToken.None);

        await Assert.That(evidence!.IdentityConsistent).IsFalse();
        await Assert.That(evidence.Pid).IsNull();
        await Assert.That(evidence.InstanceId).IsNull();
    }

    [Test]
    public async Task OneShot_calls_probe_with_the_requests_daemon_name_and_its_own_timeout() {
        string? seenName = null;
        TimeSpan? seenTimeout = null;
        var adapter = new OneShotObservation(TimeSpan.FromSeconds(3)) {
            Probe = (name, timeout, _) => {
                seenName = name;
                seenTimeout = timeout;
                return Task.FromResult(new ProbeResult(false, null, null, false));
            }
        };

        await adapter.ObserveAsync(Req(daemonName: "daemon-x"), CancellationToken.None);

        await Assert.That(seenName).IsEqualTo("daemon-x");
        await Assert.That(seenTimeout).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    // --- LiveGraphObservation ---

    [Test]
    public async Task LiveGraph_name_mismatch_returns_null() {
        var client = new FakeDaemonClientService { DaemonName = "daemon-other" };
        var adapter = new LiveGraphObservation(client);

        var evidence = await adapter.ObserveAsync(Req(daemonName: "daemon-a"), CancellationToken.None);

        await Assert.That(evidence).IsNull();
    }

    [Test]
    public async Task LiveGraph_server_mismatch_returns_null() {
        var client = new FakeDaemonClientService { DaemonName = "daemon-a" };
        client.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap("daemon-a", serverUrl: "http://localhost:1111"));
        client.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["status/1"]));
        var adapter = new LiveGraphObservation(client);

        var evidence = await adapter.ObserveAsync(Req(daemonName: "daemon-a", server: "http://localhost:9999"), CancellationToken.None);

        await Assert.That(evidence).IsNull();
    }

    [Test]
    public async Task LiveGraph_connected_matching_identity_is_consistent() {
        var client = new FakeDaemonClientService { DaemonName = "daemon-a" };
        var snap = FakeDaemonClientService.Snap("daemon-a", serverUrl: "http://localhost:9999", pid: 111, instanceId: "inst-1");
        var identity = new ConnectedIdentity(111, "inst-1", "daemon-a", "1.2.3");
        var caps = new List<string> { "status/1" }; // same reference used below — IReadOnlyList members compare by reference
        client.SnapshotsSubject.OnNext(snap);
        client.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, caps, null, identity));
        var adapter = new LiveGraphObservation(client);

        var evidence = await adapter.ObserveAsync(Req(daemonName: "daemon-a", server: "http://localhost:9999"), CancellationToken.None);

        await Assert.That(evidence).IsEqualTo(new ObservedEvidence(
            true, caps, "1.2.3", "http://localhost:9999", "daemon-a", 111, "inst-1", true));
    }

    [Test]
    public async Task LiveGraph_connected_hello_snapshot_pid_mismatch_is_inconsistent() {
        var client = new FakeDaemonClientService { DaemonName = "daemon-a" };
        var snap = FakeDaemonClientService.Snap("daemon-a", serverUrl: "http://localhost:9999", pid: 111, instanceId: "inst-1");
        var identity = new ConnectedIdentity(222, "inst-1", "daemon-a", "1.2.3"); // pid disagrees with the snapshot
        client.SnapshotsSubject.OnNext(snap);
        client.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["status/1"], null, identity));
        var adapter = new LiveGraphObservation(client);

        var evidence = await adapter.ObserveAsync(Req(daemonName: "daemon-a", server: "http://localhost:9999"), CancellationToken.None);

        await Assert.That(evidence!.Reachable).IsTrue();
        await Assert.That(evidence.IdentityConsistent).IsFalse();
    }

    [Test]
    public async Task LiveGraph_non_connected_is_unreachable_evidence() {
        var client = new FakeDaemonClientService { DaemonName = "daemon-a" };
        client.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap("daemon-a", serverUrl: "http://localhost:9999"));
        client.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        var adapter = new LiveGraphObservation(client);

        var evidence = await adapter.ObserveAsync(Req(daemonName: "daemon-a", server: "http://localhost:9999"), CancellationToken.None);

        await Assert.That(evidence).IsEqualTo(new ObservedEvidence(false, null, null, null, null, null, null, false));
    }
}
