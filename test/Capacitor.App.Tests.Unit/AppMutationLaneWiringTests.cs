using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using AppUnderTest = Capacitor.App.App;

namespace Capacitor.App.Tests.Unit;

/// Task 10: the composition-root helpers wiring the mutation lane into App.axaml.cs — the
/// outcome-channel presentation routing, the cliOverride null-remap, and the shutdown
/// quiesce composition. Plain TUnit, no Avalonia session needed (these are pure/async functions
/// over interfaces and fakes — FakeLifecycleSurface/FakeKcapCli/FakeLoginShellProbe are shared
/// from DaemonLifecycleControllerTests.cs, same namespace).
public class AppMutationLaneWiringTests {
    // ---- ResolveCliOverride's testable core ----

    [Test]
    public async Task MapBareKcapToNull_maps_the_unset_fallback_to_null() {
        await Assert.That(AppUnderTest.MapBareKcapToNull("kcap")).IsNull();
    }

    [Test]
    public async Task MapBareKcapToNull_passes_a_real_override_through_verbatim() {
        await Assert.That(AppUnderTest.MapBareKcapToNull("/opt/kcap/kcap")).IsEqualTo("/opt/kcap/kcap");
    }

    [Test]
    public async Task MapBareKcapToNull_passes_a_broken_override_null_through() {
        // CliResolver.ResolvePath already reports a broken override as null — must stay null, not "kcap".
        await Assert.That(AppUnderTest.MapBareKcapToNull(null)).IsNull();
    }

    // ---- PresentOutcomeAsync / ClassifyForPresentation ----

    static OutcomeEnvelope Envelope(MutationOutcome outcome) =>
        new(new MutationRequest(MutationVerb.StartVerified, "default", "https://kcap.example.com:443", "daemon-a"), outcome);

    [Test]
    public async Task Takeover_surface_shows_the_dialog_and_names_the_token() {
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) };
        var envelope = Envelope(new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover));

        await AppUnderTest.PresentOutcomeAsync(surface, envelope, CancellationToken.None);

        await Assert.That(surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindTakeover);
        await Assert.That(surface.Prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.TakeoverDisclosure);
        await Assert.That(surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(surface.StatusMessages[0]).Contains("foreign_binary");
        await Assert.That(surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Reinstall_surface_is_a_status_line_naming_the_token_no_dialog() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.Failed(28, "package_inconsistent", RecoverySurface.Reinstall));

        await AppUnderTest.PresentOutcomeAsync(surface, envelope, CancellationToken.None);

        await Assert.That(surface.Prompts).IsEmpty();
        await Assert.That(surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(surface.StatusMessages[0]).Contains("package_inconsistent");
        await Assert.That(surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Attention_surface_names_the_token() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.Failed(1, "internal_error", RecoverySurface.Attention));

        await AppUnderTest.PresentOutcomeAsync(surface, envelope, CancellationToken.None);

        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains("internal_error");
        await Assert.That(surface.Prompts).IsEmpty();
        await Assert.That(surface.StatusMessages).IsEmpty();
    }

    [Test]
    public async Task Storage_surface_also_reads_as_attention_and_names_the_token() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.Refused("consent_seed_unwritable", RecoverySurface.Storage));

        await AppUnderTest.PresentOutcomeAsync(surface, envelope, CancellationToken.None);

        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains("consent_seed_unwritable");
    }

    [Test]
    public async Task AttentionSkew_always_reads_as_attention_naming_its_own_detail() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.AttentionSkew("ownership_mismatch"));

        await AppUnderTest.PresentOutcomeAsync(surface, envelope, CancellationToken.None);

        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains("ownership_mismatch");
    }

    [Test]
    public async Task AttentionRepair_always_reads_as_attention_naming_its_own_detail() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.AttentionRepair("stale_txn_marker"));

        await AppUnderTest.PresentOutcomeAsync(surface, envelope, CancellationToken.None);

        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains("stale_txn_marker");
    }

    [Test]
    public async Task UnconfirmedNoAttach_is_never_presented() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.UnconfirmedNoAttach());

        await AppUnderTest.PresentOutcomeAsync(surface, envelope, CancellationToken.None);

        await Assert.That(surface.Prompts).IsEmpty();
        await Assert.That(surface.StatusMessages).IsEmpty();
        await Assert.That(surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Failed_with_no_reason_token_falls_back_to_the_exit_code_token() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.Failed(24, null, RecoverySurface.Attention));

        await AppUnderTest.PresentOutcomeAsync(surface, envelope, CancellationToken.None);

        await Assert.That(surface.AttentionMessages[0]).Contains("verify_readiness_timeout");
    }

    // ---- ClassifyForPresentation (the pure routing table, standalone) ----

    [Test]
    public async Task ClassifyForPresentation_reads_Refused_and_Failed_off_their_own_surface_field() {
        var (surface1, token1) = AppUnderTest.ClassifyForPresentation(new MutationOutcome.Refused("no_server_configured", RecoverySurface.Attention));
        await Assert.That(surface1).IsEqualTo(RecoverySurface.Attention);
        await Assert.That(token1).IsEqualTo("no_server_configured");

        var (surface2, token2) = AppUnderTest.ClassifyForPresentation(new MutationOutcome.Failed(28, "identity_mismatch", RecoverySurface.Takeover));
        await Assert.That(surface2).IsEqualTo(RecoverySurface.Takeover);
        await Assert.That(token2).IsEqualTo("identity_mismatch");
    }

    [Test]
    public async Task ClassifyForPresentation_success_cases_are_None() {
        var (surface1, _) = AppUnderTest.ClassifyForPresentation(new MutationOutcome.Succeeded());
        await Assert.That(surface1).IsEqualTo(RecoverySurface.None);

        var (surface2, _) = AppUnderTest.ClassifyForPresentation(new MutationOutcome.SucceededAfterTimeout());
        await Assert.That(surface2).IsEqualTo(RecoverySurface.None);
    }

    // ---- QuiesceLifecycleAndLaneAsync (shutdown composition) ----

    [Test]
    public async Task QuiesceLifecycleAndLaneAsync_with_nothing_live_completes_immediately() {
        await AppUnderTest.QuiesceLifecycleAndLaneAsync(null, null).WaitAsync(TimeSpan.FromSeconds(5));
    }

    sealed class NeverObservation : IDaemonObservation {
        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) =>
            Task.FromResult<ObservedEvidence?>(null);
    }

    // Proves the shutdown composition actually covers the lane's OWN in-flight work — not just
    // the controller's gate — since DaemonClientService.StartDaemonAsync calls the lane directly,
    // never through the controller at all (the reason this composition exists, ruling 6).
    [Test]
    public async Task QuiesceLifecycleAndLaneAsync_waits_for_an_in_flight_lane_mutation() {
        var gate = new TaskCompletionSource<string?>();
        var cli = new FakeKcapCli { VersionBehavior = _ => gate.Task };
        await using var lane = new DaemonMutationLane(
            new FakeLoginShellProbe { KcapPathBehavior = _ => Task.FromResult<string?>(null) },
            new OutcomeChannel(),
            () => "/opt/kcap/bin/kcap",
            (_, _) => cli,
            _ => new NeverObservation(),
            TimeProvider.System);

        var request = new MutationRequest(MutationVerb.StartVerified, "default", "https://kcap.example.com:443", "daemon-a");
        var runTask = lane.RunAsync(request, CancellationToken.None);

        var quiesced = AppUnderTest.QuiesceLifecycleAndLaneAsync(null, lane);
        await Task.Delay(50);
        await Assert.That(quiesced.IsCompleted).IsFalse();

        gate.SetResult("9.9.9");
        await quiesced.WaitAsync(TimeSpan.FromSeconds(5));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
