using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// Attempt-scoped attribution matrix (task-6 brief Step 1) for the app's lane-owned boot-refusal reader.
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class BootRefusalMarkerTests {
    const string DaemonName = "boot-refusal-app-test";

    static string Json(string daemonName, string? attemptId) => $$"""
        {"daemon_name":"{{daemonName}}","token":"server_expectation_mismatch","expectation":"https://s","resolved":"https://t","pid":4242,"instance_id":"inst-1","attempt_id":{{(attemptId is null ? "null" : $"\"{attemptId}\"")}}}
        """;

    static async Task Run(Func<string, Task> body) {
        var dir = Directory.CreateTempSubdirectory("boot-refusal-app-").FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            await body(dir);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    static void PlantMarker(string content) {
        var path = BootRefusalMarker.MarkerPath(DaemonName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Test]
    public async Task Matching_attempt_is_attributed_and_consumes_the_marker() {
        await Run(async _ => {
            PlantMarker(Json(DaemonName, "att-1"));

            var evidence = BootRefusalMarker.TryAttribute(DaemonName, "att-1");

            await Assert.That(evidence).IsNotNull();
            await Assert.That(evidence!.AttemptId).IsEqualTo("att-1");
            await Assert.That(File.Exists(BootRefusalMarker.MarkerPath(DaemonName))).IsFalse();
        });
    }

    [Test]
    public async Task Different_attempt_is_not_attributed_and_marker_is_retained() {
        await Run(async _ => {
            PlantMarker(Json(DaemonName, "att-1"));

            var evidence = BootRefusalMarker.TryAttribute(DaemonName, "att-2");

            await Assert.That(evidence).IsNull();
            await Assert.That(File.Exists(BootRefusalMarker.MarkerPath(DaemonName))).IsTrue();
        });
    }

    [Test]
    public async Task Null_attempt_id_is_never_attributed() {
        await Run(async _ => {
            PlantMarker(Json(DaemonName, null));

            var evidence = BootRefusalMarker.TryAttribute(DaemonName, "att-1");

            await Assert.That(evidence).IsNull();
            await Assert.That(File.Exists(BootRefusalMarker.MarkerPath(DaemonName))).IsTrue();
        });
    }

    [Test]
    public async Task Corrupt_json_reads_null_and_is_left_in_place() {
        await Run(async _ => {
            PlantMarker("{not json");

            await Assert.That(BootRefusalMarker.TryRead(DaemonName)).IsNull();
            await Assert.That(BootRefusalMarker.TryAttribute(DaemonName, "att-1")).IsNull();
            await Assert.That(File.Exists(BootRefusalMarker.MarkerPath(DaemonName))).IsTrue();
        });
    }

    [Test]
    public async Task Absent_marker_reads_null() {
        await Run(async _ => {
            await Assert.That(BootRefusalMarker.TryRead(DaemonName)).IsNull();
            await Assert.That(BootRefusalMarker.TryAttribute(DaemonName, "att-1")).IsNull();
        });
    }

    [Test]
    public async Task Foreign_daemon_name_inside_the_record_is_never_attributed() {
        await Run(async _ => {
            PlantMarker(Json("some-other-daemon", "att-1"));

            var evidence = BootRefusalMarker.TryAttribute(DaemonName, "att-1");

            await Assert.That(evidence).IsNull();
            await Assert.That(File.Exists(BootRefusalMarker.MarkerPath(DaemonName))).IsTrue();
        });
    }

    [Test]
    public async Task Valid_marker_round_trips_via_TryRead() {
        await Run(async _ => {
            PlantMarker(Json(DaemonName, "att-1"));

            var evidence = BootRefusalMarker.TryRead(DaemonName);

            await Assert.That(evidence).IsNotNull();
            await Assert.That(evidence!.DaemonName).IsEqualTo(DaemonName);
            await Assert.That(evidence.Token).IsEqualTo("server_expectation_mismatch");
            await Assert.That(evidence.Expectation).IsEqualTo("https://s");
            await Assert.That(evidence.Resolved).IsEqualTo("https://t");
            await Assert.That(evidence.Pid).IsEqualTo(4242);
            await Assert.That(evidence.InstanceId).IsEqualTo("inst-1");
            await Assert.That(evidence.AttemptId).IsEqualTo("att-1");
        });
    }
}
