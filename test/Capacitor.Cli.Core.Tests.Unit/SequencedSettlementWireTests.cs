using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

public class SequencedSettlementWireTests {
    static readonly JsonSerializerOptions Opts = new() {
        TypeInfoResolver = CapacitorJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Test]
    public async Task StartupDiscovery_pins_exact_wire_tokens_and_defaults_to_pending() {
        var json = JsonSerializer.Serialize(new StartupDiscovery(MarkerScanState.Complete), Opts);
        await Assert.That(json).Contains("\"marker_scan_state\":\"complete\"");

        // The zero value (missing field) is the conservative default.
#pragma warning disable TUnitAssertions0005
        await Assert.That(default(MarkerScanState)).IsEqualTo(MarkerScanState.Pending);
#pragma warning restore TUnitAssertions0005
    }

    [Test]
    public async Task Resolved_and_unresolved_candidates_round_trip() {
        var resolved = new ResolvedStartupCandidate(7, "a1", "old-epoch", "flow-1", "reviewer");
        var rt = JsonSerializer.Deserialize<ResolvedStartupCandidate>(JsonSerializer.Serialize(resolved, Opts), Opts);
        await Assert.That(rt).IsEqualTo(resolved);

        var blockedJson = JsonSerializer.Serialize(
            new UnresolvedStartupCandidate("a2", StartupCandidateUnresolvedReason.PendingMarker), Opts);
        await Assert.That(blockedJson).Contains("\"reason\":\"pending_marker\"");
    }

    [Test]
    public async Task DaemonStatusReport_new_fields_round_trip_and_default_null() {
        // Old-shape construction still compiles (all new args optional).
        var old = new DaemonStatusReport(2, [], []);
        await Assert.That(old.Epoch).IsNull();
        await Assert.That(old.StartupDiscovery).IsNull();

        var full = new DaemonStatusReport(
            2, [], [], Epoch: "e1", LastProcessedSeq: 5, HighestAcceptedSeq: 6,
            StartupReapComplete: true, ResolvedStartupCandidates: [],
            UnresolvedStartupCandidates: [], StartupDiscovery: new StartupDiscovery(MarkerScanState.Complete));
        var rt = JsonSerializer.Deserialize<DaemonStatusReport>(JsonSerializer.Serialize(full, Opts), Opts);
        await Assert.That(rt.Epoch).IsEqualTo("e1");
        await Assert.That(rt.StartupReapComplete).IsTrue();
        await Assert.That(rt.StartupDiscovery!.Value.MarkerScanState).IsEqualTo(MarkerScanState.Complete);
    }

    [Test]
    public async Task Command_dtos_pin_wire_tokens_and_round_trip() {
        var stop = new StopAgentV2("a1", "e1", 9, "cmd-guid");
        var rt = JsonSerializer.Deserialize<StopAgentV2>(JsonSerializer.Serialize(stop, Opts), Opts);
        await Assert.That(rt).IsEqualTo(stop);

        var ackJson = JsonSerializer.Serialize(
            new CommandAck("e1", 9, "cmd", CommandAckState.Processed,
                CommandOutcomeKind.LaunchExecuted, AgentLiveness.Live, "a1", "sess"), Opts);
        await Assert.That(ackJson).Contains("\"state\":\"processed\"");
        await Assert.That(ackJson).Contains("\"outcome_kind\":\"launch_executed\"");
        await Assert.That(ackJson).Contains("\"current_state\":\"live\"");

        var rejJson = JsonSerializer.Serialize(
            new CommandRejected("e1", 9, "cmd", CommandRejectedReason.StaleEpoch, "a1"), Opts);
        await Assert.That(rejJson).Contains("\"reason\":\"stale_epoch\"");

        var pruneJson = JsonSerializer.Serialize(
            new AckResolvedCandidates([new ResolvedCandidateAck(3, "a1", "old")]), Opts);
        await Assert.That(pruneJson).Contains("\"generation\":3");
    }

    // Phase B2-b (sequenced-settlement design §5.5): the report/connect payloads advertise
    // HighestResolutionGeneration (the daemon-lifetime monotonic high-water) alongside the resolved-
    // candidates array, so once sparse acks prune entries the server still knows the generation frontier.
    // Additive/optional — old-shape construction leaves it null.
    [Test]
    public async Task HighestResolutionGeneration_round_trips_on_report_and_connect() {
        var reportRt = JsonSerializer.Deserialize<DaemonStatusReport>(JsonSerializer.Serialize(
            new DaemonStatusReport(1, [], [], HighestResolutionGeneration: 7), Opts), Opts);
        await Assert.That(reportRt.HighestResolutionGeneration).IsEqualTo(7L);

        // Old-shape construction still leaves it null.
        await Assert.That(new DaemonStatusReport(1, [], []).HighestResolutionGeneration).IsNull();

        var connectRt = JsonSerializer.Deserialize<DaemonConnect>(JsonSerializer.Serialize(
            new DaemonConnect("d", "linux", [], 5, [], HighestResolutionGeneration: 9), Opts), Opts);
        await Assert.That(connectRt.HighestResolutionGeneration).IsEqualTo(9L);
        await Assert.That(new DaemonConnect("d", "linux", [], 5, []).HighestResolutionGeneration).IsNull();
    }

    [Test]
    public async Task LaunchAgentCommand_gains_optional_sequencing_fields() {
        var legacy = new LaunchAgentCommand("a1", "hi", "opus", null, "/repo", null, null, "claude");
        await Assert.That(legacy.Seq).IsNull(); // absent ⇒ legacy lane

        var seqd = legacy with { Epoch = "e1", Seq = 4, CommandId = "cmd" };
        var rt = JsonSerializer.Deserialize<LaunchAgentCommand>(JsonSerializer.Serialize(seqd, Opts), Opts);
        await Assert.That(rt.Seq).IsEqualTo(4L);
        await Assert.That(rt.CommandId).IsEqualTo("cmd");
    }
}
