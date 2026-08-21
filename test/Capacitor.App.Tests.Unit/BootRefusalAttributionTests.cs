using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// Attempt-scoped attribution matrix for the app's lane-owned boot-refusal reader — every
/// attribution requires a VERIFIABLE identity (schema, attempt/daemon-name/token/pid/instance-id,
/// and the marker's own Expectation matching the request's canonical server), cross-checked
/// against the shape the writer produces (Capacitor.Cli.Core.BootRefusalMarker).
public class BootRefusalAttributionTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    const string DaemonName = "boot-refusal-app-test";
    const string Expectation = "https://s";

    static string Json(
            string daemonName, string? attemptId, int schema = 1, string? expectation = Expectation,
            string token = "server_expectation_mismatch", int pid = 4242, string? instanceId = "inst-1") => $$"""
        {"schema":{{schema}},"daemon_name":"{{daemonName}}","token":"{{token}}","expectation":{{(expectation is null ? "null" : $"\"{expectation}\"")}},"resolved":"https://t","pid":{{pid}},"instance_id":{{(instanceId is null ? "null" : $"\"{instanceId}\"")}},"attempt_id":{{(attemptId is null ? "null" : $"\"{attemptId}\"")}}}
        """;

    void PlantMarker(string content) {
        Directory.CreateDirectory(Daemons.Store.StateDirectory(DaemonName));
        File.WriteAllText(Daemons.Store.BootRefusalPath(DaemonName), content);
    }

    [Test]
    public async Task Matching_attempt_and_identity_is_attributed_and_consumes_the_marker() {
        PlantMarker(Json(DaemonName, "att-1"));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation);

        await Assert.That(evidence).IsNotNull();
        await Assert.That(evidence!.AttemptId).IsEqualTo("att-1");
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsFalse();
    }

    [Test]
    public async Task Different_attempt_is_not_attributed_and_marker_is_retained() {
        PlantMarker(Json(DaemonName, "att-1"));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-2", Expectation);

        await Assert.That(evidence).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    [Test]
    public async Task Null_attempt_id_is_never_attributed() {
        PlantMarker(Json(DaemonName, null));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation);

        await Assert.That(evidence).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    [Test]
    public async Task Corrupt_json_reads_null_and_is_left_in_place() {
        PlantMarker("{not json");

        await Assert.That(BootRefusalMarker.TryRead(Daemons.Store, DaemonName)).IsNull();
        await Assert.That(BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation)).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    [Test]
    public async Task Absent_marker_reads_null() {
        await Assert.That(BootRefusalMarker.TryRead(Daemons.Store, DaemonName)).IsNull();
        await Assert.That(BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation)).IsNull();
    }

    [Test]
    public async Task Foreign_daemon_name_inside_the_record_is_never_attributed() {
        PlantMarker(Json("some-other-daemon", "att-1"));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation);

        await Assert.That(evidence).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    // ---- P2-4: identity-validation rejection arms, each retaining the marker ----

    [Test]
    public async Task Wrong_schema_is_never_attributed() {
        PlantMarker(Json(DaemonName, "att-1", schema: 2));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation);

        await Assert.That(evidence).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    [Test]
    public async Task Empty_token_is_never_attributed() {
        PlantMarker(Json(DaemonName, "att-1", token: ""));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation);

        await Assert.That(evidence).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    [Test]
    public async Task NonPositive_pid_is_never_attributed() {
        PlantMarker(Json(DaemonName, "att-1", pid: 0));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation);

        await Assert.That(evidence).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    [Test]
    public async Task Empty_instance_id_is_never_attributed() {
        PlantMarker(Json(DaemonName, "att-1", instanceId: ""));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation);

        await Assert.That(evidence).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    [Test]
    public async Task Null_instance_id_is_never_attributed() {
        PlantMarker(Json(DaemonName, "att-1", instanceId: null));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation);

        await Assert.That(evidence).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    [Test]
    public async Task Expectation_mismatch_against_the_requests_canonical_server_is_never_attributed() {
        PlantMarker(Json(DaemonName, "att-1", expectation: "https://other"));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation);

        await Assert.That(evidence).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    [Test]
    public async Task Null_expectation_against_a_real_request_canonical_server_is_never_attributed() {
        PlantMarker(Json(DaemonName, "att-1", expectation: null));

        var evidence = BootRefusalAttribution.TryAttribute(Daemons.Store, DaemonName, "att-1", Expectation);

        await Assert.That(evidence).IsNull();
        await Assert.That(File.Exists(Daemons.Store.BootRefusalPath(DaemonName))).IsTrue();
    }

    [Test]
    public async Task Valid_marker_round_trips_via_TryRead() {
        PlantMarker(Json(DaemonName, "att-1"));

        var evidence = BootRefusalMarker.TryRead(Daemons.Store, DaemonName);

        await Assert.That(evidence).IsNotNull();
        await Assert.That(evidence!.Schema).IsEqualTo(1);
        await Assert.That(evidence.DaemonName).IsEqualTo(DaemonName);
        await Assert.That(evidence.Token).IsEqualTo("server_expectation_mismatch");
        await Assert.That(evidence.Expectation).IsEqualTo(Expectation);
        await Assert.That(evidence.Resolved).IsEqualTo("https://t");
        await Assert.That(evidence.Pid).IsEqualTo(4242);
        await Assert.That(evidence.InstanceId).IsEqualTo("inst-1");
        await Assert.That(evidence.AttemptId).IsEqualTo("att-1");
    }
}
