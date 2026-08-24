using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using AppUnderTest = Capacitor.App.App;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// Task 10: the composition-root helpers wiring the mutation lane into App.axaml.cs — the
/// outcome-channel presentation routing, the cliOverride absolute-pin resolution, and the
/// shutdown quiesce composition. Plain TUnit, no Avalonia session needed (these are pure/async
/// functions over interfaces and fakes — FakeLifecycleSurface/FakeKcapCli/FakeLoginShellProbe are
/// shared from DaemonLifecycleControllerTests.cs, same namespace).
public class AppMutationLaneWiringTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    static MutationRequest Req(MutationVerb verb = MutationVerb.StartVerified) =>
        new(verb, "default", "https://kcap.example.com:443", "daemon-a");

    static OutcomeEnvelope Envelope(MutationOutcome outcome, MutationVerb verb = MutationVerb.StartVerified) =>
        new(Req(verb), outcome);

    // A tripwire for "must not be called" assertions — round-1 review C-1's Decline case, and
    // every non-Takeover branch, must never re-mutate.
    static Task<MutationOutcome> NeverRunMutation(MutationRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("runMutation must not be called");

    static Func<CancellationToken, Task<string?>> FixedTerminalPath(string? path) => _ => Task.FromResult(path);

    // ---- ResolveCliOverrideCore (round-1 review M-4) ----

    [Test]
    public async Task ResolveCliOverrideCore_no_override_is_null() {
        await Assert.That(AppUnderTest.ResolveCliOverrideCore(null, _ => true, p => p)).IsNull();
    }

    [Test]
    public async Task ResolveCliOverrideCore_empty_override_is_null() {
        await Assert.That(AppUnderTest.ResolveCliOverrideCore("", _ => true, p => p)).IsNull();
    }

    [Test]
    public async Task ResolveCliOverrideCore_existing_override_is_absolute_pinned() {
        // Path.GetTempPath() is a real absolute root on every platform (a Windows leg exercises
        // this with its own drive/backslash form) — Path.Combine, not a hardcoded Unix literal,
        // builds both the fake getFullPath's answer and the expectation.
        var absRoot = Path.Combine(Path.GetTempPath(), "abs-root");
        var overrideEnv = Path.Combine(".", "bin", "kcap");
        var result = AppUnderTest.ResolveCliOverrideCore(overrideEnv, _ => true, p => Path.Combine(absRoot, p));
        await Assert.That(result).IsEqualTo(Path.Combine(absRoot, overrideEnv));
    }

    [Test]
    public async Task ResolveCliOverrideCore_broken_override_is_null_fail_closed() {
        await Assert.That(AppUnderTest.ResolveCliOverrideCore("/opt/kcap/kcap", _ => false, p => p)).IsNull();
    }

    // The whole point of M-4: a real override whose value happens to equal the OLD no-override
    // sentinel string ("kcap") must still resolve as a real, absolute-pinned override — not be
    // silently treated as "no override set" via a string-compare ambiguity.
    [Test]
    public async Task ResolveCliOverrideCore_override_literally_named_kcap_is_not_confused_with_the_old_sentinel() {
        var absRoot = Path.Combine(Path.GetTempPath(), "abs-root");
        var result = AppUnderTest.ResolveCliOverrideCore("kcap", _ => true, p => Path.Combine(absRoot, p));
        await Assert.That(result).IsEqualTo(Path.Combine(absRoot, "kcap"));
    }

    // ---- PresentOutcomeAsync: Takeover (round-1 review C-1 / C-2) ----

    [Test]
    public async Task Takeover_accept_issues_exactly_one_Replace_request_at_the_envelopes_own_identity() {
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) };
        var envelope = Envelope(new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover));
        var seen = new List<MutationRequest>();
        Task<MutationOutcome> RunMutation(MutationRequest r, CancellationToken ct) {
            seen.Add(r);
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        }

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, RunMutation, FixedTerminalPath("/usr/bin:/bin"), () => "1.2.3", CancellationToken.None);

        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen[0].Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(seen[0].Profile).IsEqualTo(envelope.Request.Profile);
        await Assert.That(seen[0].CanonicalServer).IsEqualTo(envelope.Request.CanonicalServer);
        await Assert.That(seen[0].DaemonName).IsEqualTo(envelope.Request.DaemonName);
        await Assert.That(surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindTakeover);
        await Assert.That(surface.Prompts[0].CliVersion).IsEqualTo("1.2.3");
        await Assert.That(surface.Prompts[0].PathDegraded).IsFalse();
        await Assert.That(surface.Prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.TakeoverDisclosure);
        await Assert.That(surface.StatusMessages).IsEmpty(); // accept never writes Status — the dialog IS the presentation
    }

    [Test]
    public async Task Takeover_accept_discloses_PathDegraded_true_and_null_CliVersion_when_unavailable() {
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) };
        var envelope = Envelope(new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover));

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            FixedTerminalPath(null), () => null, CancellationToken.None);

        await Assert.That(surface.Prompts[0].PathDegraded).IsTrue();
        await Assert.That(surface.Prompts[0].CliVersion).IsNull();
    }

    [Test]
    public async Task Takeover_decline_issues_no_request_and_exactly_one_status_line_naming_the_token() {
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(false) };
        var envelope = Envelope(new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover));

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => "1.2.3", CancellationToken.None);

        await Assert.That(surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(surface.StatusMessages[0]).Contains("foreign_binary");
        await Assert.That(surface.AttentionMessages).IsEmpty();
    }

    // ---- PresentOutcomeAsync: Reinstall/Attention/Storage (round-1 review C-2b) ----

    [Test]
    public async Task Reinstall_surface_is_an_attention_line_naming_the_token_no_dialog_no_status() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.Failed(28, "package_inconsistent", RecoverySurface.Reinstall));

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, CancellationToken.None);

        await Assert.That(surface.Prompts).IsEmpty();
        await Assert.That(surface.StatusMessages).IsEmpty(); // moved OFF the status slot per C-2
        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains("package_inconsistent");
    }

    [Test]
    public async Task Attention_surface_names_the_token() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.Failed(1, "internal_error", RecoverySurface.Attention));

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, CancellationToken.None);

        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains("internal_error");
        await Assert.That(surface.Prompts).IsEmpty();
        await Assert.That(surface.StatusMessages).IsEmpty();
    }

    [Test]
    public async Task Storage_surface_also_reads_as_attention_and_names_the_token() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.Refused("consent_seed_unwritable", RecoverySurface.Storage));

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, CancellationToken.None);

        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains("consent_seed_unwritable");
    }

    [Test]
    public async Task AttentionSkew_always_reads_as_attention_naming_its_own_detail() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.AttentionSkew("ownership_mismatch"));

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, CancellationToken.None);

        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains("ownership_mismatch");
    }

    [Test]
    public async Task AttentionRepair_always_reads_as_attention_naming_its_own_detail() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.AttentionRepair("stale_txn_marker"));

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, CancellationToken.None);

        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains("stale_txn_marker");
    }

    // round-1 review I-3: UnconfirmedNoAttach IS actionable — one attention presentation naming
    // the verb (Succeeded/SucceededAfterTimeout still never reach the channel at all). round-2
    // review R2-3: the verb is rendered through a small display map, not MutationVerb.ToString().
    [Test]
    public async Task UnconfirmedNoAttach_is_presented_once_naming_the_verb() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.UnconfirmedNoAttach(), MutationVerb.DetachedStart);

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, CancellationToken.None);

        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains("daemon start");
        await Assert.That(surface.Prompts).IsEmpty();
        await Assert.That(surface.StatusMessages).IsEmpty();
    }

    [Test]
    [Arguments(MutationVerb.Install, "install")]
    [Arguments(MutationVerb.Replace, "replace")]
    [Arguments(MutationVerb.StartVerified, "verified start")]
    [Arguments(MutationVerb.DetachedStart, "daemon start")]
    public async Task UnconfirmedNoAttach_names_every_verb_via_the_display_map(MutationVerb verb, string expectedDisplay) {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.UnconfirmedNoAttach(), verb);

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, CancellationToken.None);

        await Assert.That(surface.AttentionMessages[0]).Contains(expectedDisplay);
    }

    [Test]
    public async Task Failed_with_no_reason_token_falls_back_to_the_exit_code_token() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.Failed(24, null, RecoverySurface.Attention));

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, CancellationToken.None);

        await Assert.That(surface.AttentionMessages[0]).Contains("verify_readiness_timeout");
    }

    // M2: DigestGate (43) is DaemonCommands' own gate, not a ServiceVerify exit code, but it still
    // needs a real presentable token when the CLI emits no reason line — falling back to
    // "verify_unknown_43" would surface a meaningless code to the user.
    [Test]
    public async Task Failed_digest_gate_with_no_reason_token_falls_back_to_the_daemon_start_gate_token() {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.Failed(VerifyExitCodes.DigestGate, null, RecoverySurface.Attention));

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, CancellationToken.None);

        await Assert.That(surface.AttentionMessages[0]).Contains("daemon_start_gate");
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

    // P2-5 / spec §10: the closed set of AttentionSkew tokens meaning "connected but below the
    // floor this app requires" — read verbatim off DaemonMutationLane.EvidenceFailureLeg — routes
    // to Takeover; every other AttentionSkew detail stays non-destructive Attention.
    [Test]
    [Arguments("missing_capability_consent_3")]
    [Arguments("daemon_below_floor")]
    [Arguments("pre_slice_evidence")]
    public async Task ClassifyForPresentation_routes_the_closed_set_of_AttentionSkew_tokens_to_Takeover(string token) {
        var (surface, detail) = AppUnderTest.ClassifyForPresentation(new MutationOutcome.AttentionSkew(token));
        await Assert.That(surface).IsEqualTo(RecoverySurface.Takeover);
        await Assert.That(detail).IsEqualTo(token);
    }

    [Test]
    [Arguments("server_or_name_mismatch")]
    [Arguments("identity_inconsistent")]
    [Arguments("unreachable_with_recorded_owner")]
    [Arguments("ownership_unknown")]
    [Arguments("ownership_mismatch")]
    [Arguments("instance_pid_mismatch")]
    [Arguments("some_unknown_future_token")]
    public async Task ClassifyForPresentation_routes_every_other_AttentionSkew_token_to_Attention(string token) {
        var (surface, detail) = AppUnderTest.ClassifyForPresentation(new MutationOutcome.AttentionSkew(token));
        await Assert.That(surface).IsEqualTo(RecoverySurface.Attention);
        await Assert.That(detail).IsEqualTo(token);
    }

    // ---- PresentOutcomeAsync: AttentionSkew routed to Takeover (P2-5) ----

    [Test]
    [Arguments("missing_capability_consent_3")]
    [Arguments("daemon_below_floor")]
    [Arguments("pre_slice_evidence")]
    public async Task AttentionSkew_known_token_prompts_takeover_and_accept_issues_Replace_at_envelope_identity(string token) {
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) };
        var envelope = Envelope(new MutationOutcome.AttentionSkew(token));
        var seen = new List<MutationRequest>();
        Task<MutationOutcome> RunMutation(MutationRequest r, CancellationToken ct) {
            seen.Add(r);
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        }

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, RunMutation, FixedTerminalPath("/usr/bin"), () => "1.2.3", CancellationToken.None);

        await Assert.That(surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindTakeover);
        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen[0].Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(seen[0].Profile).IsEqualTo(envelope.Request.Profile);
        await Assert.That(seen[0].CanonicalServer).IsEqualTo(envelope.Request.CanonicalServer);
        await Assert.That(seen[0].DaemonName).IsEqualTo(envelope.Request.DaemonName);
    }

    [Test]
    [Arguments("server_or_name_mismatch")]
    [Arguments("ownership_mismatch")]
    [Arguments("unreachable_with_recorded_owner")]
    [Arguments("some_unknown_future_token")]
    public async Task AttentionSkew_other_token_is_attention_only_no_dialog(string token) {
        var surface = new FakeLifecycleSurface();
        var envelope = Envelope(new MutationOutcome.AttentionSkew(token));

        await AppUnderTest.PresentOutcomeAsync(
            surface, envelope, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, CancellationToken.None);

        await Assert.That(surface.Prompts).IsEmpty();
        await Assert.That(surface.AttentionMessages.Count).IsEqualTo(1);
        await Assert.That(surface.AttentionMessages[0]).Contains(token);
    }

    // ---- ConsumeMutationOutcomesAsync (round-1 review I-1) ----

    /// Throws on its FIRST Attention call only, then behaves normally — simulates a presentation
    /// failure for exactly one envelope.
    sealed class ThrowOnceOnAttentionSurface(ILifecycleSurface inner) : ILifecycleSurface {
        bool _thrown;
        public void Status(string message) => inner.Status(message);
        public void Attention(string message) {
            if (!_thrown) { _thrown = true; throw new InvalidOperationException("boom"); }
            inner.Attention(message);
        }
        public Task<bool> ConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) => inner.ConfirmAsync(prompt, ct);
        public Task<bool?> TryConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) => inner.TryConfirmAsync(prompt, ct);
    }

    // P1-2: a presentation failure BEFORE the UI boundary (surface.Attention throwing) must requeue
    // the envelope for a re-presentation, never Ack-and-drop it — the loop itself survives either
    // way (never faulted/canceled), but the outcome itself is no longer silently skipped.
    [Test]
    public async Task ConsumeMutationOutcomesAsync_a_presentation_failure_requeues_and_re_presents_then_acks() {
        var inner = new FakeLifecycleSurface();
        var surface = new ThrowOnceOnAttentionSurface(inner);
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var consumerTask = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, surface, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, cts.Token);

        channel.Enqueue(new OutcomeEnvelope(Req(), new MutationOutcome.Failed(1, "boom1", RecoverySurface.Attention)));

        // The first attempt throws (pre-presentation) and is requeued at the front, not skipped —
        // the retry succeeds (ThrowOnceOnAttentionSurface only throws once) and is what actually
        // reaches the surface.
        await WaitUntilAsync(() => inner.AttentionMessages.Count == 1, what: "the requeued retry's presentation");
        await Assert.That(inner.AttentionMessages[0]).Contains("boom1");

        // Channel remains functional afterward: a fresh envelope still presents normally.
        channel.Enqueue(new OutcomeEnvelope(Req(), new MutationOutcome.Failed(2, "boom2", RecoverySurface.Attention)));
        await WaitUntilAsync(() => inner.AttentionMessages.Count == 2, what: "the next envelope's presentation");
        await Assert.That(inner.AttentionMessages[1]).Contains("boom2");

        cts.Cancel();
        // The loop's own outer catch swallows shutdown's OperationCanceledException by design
        // (draining just stops) — the task completes normally, never faulted/canceled: the loop
        // survives a presentation failure.
        await consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // P1-2: a ct already cancelled BEFORE the takeover dialog is ever shown (TryConfirmAsync
    // returns null) must requeue the envelope — never Ack it, never record decline memory for a
    // dialog the user never saw. A fresh consumer over the same channel still gets to present it.
    [Test]
    public async Task ConsumeMutationOutcomesAsync_cancellation_before_the_takeover_dialog_requeues_and_records_no_decline() {
        var surface = new FakeLifecycleSurface();
        var channel = new OutcomeChannel();
        using var cancelledCts = new CancellationTokenSource();

        channel.Enqueue(Envelope(new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover)));
        cancelledCts.Cancel(); // cancelled before the consumer ever starts draining this envelope

        await AppUnderTest.ConsumeMutationOutcomesAsync(
                channel, surface, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, cancelledCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(surface.Prompts).IsEmpty(); // the dialog was never shown
        await Assert.That(surface.StatusMessages).IsEmpty(); // no decline line — never declined, just never shown

        var acceptingSurface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) };
        using var liveCts = new CancellationTokenSource();
        static Task<MutationOutcome> AcceptMutation(MutationRequest r, CancellationToken ct) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        var secondConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, acceptingSurface, AcceptMutation, FixedTerminalPath("/usr/bin"), () => null, liveCts.Token);

        await WaitUntilAsync(() => acceptingSurface.Prompts.Count == 1, what: "the requeued envelope's real presentation");

        liveCts.Cancel();
        await secondConsumer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // P1-2: the takeover dialog IS the presentation boundary — once ConfirmAsync returns (shown
    // and answered), a fault in the accept's OWN re-mutation must still Ack, never requeue the
    // envelope for a second dialog.
    [Test]
    public async Task ConsumeMutationOutcomesAsync_a_fault_after_the_takeover_dialog_still_acks_not_requeues() {
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) }; // accept
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();
        var runMutationCalls = 0;
        Task<MutationOutcome> ThrowingRunMutation(MutationRequest r, CancellationToken ct) {
            runMutationCalls++;
            throw new InvalidOperationException("boom-after-accept");
        }

        var consumerTask = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, surface, ThrowingRunMutation, FixedTerminalPath("/usr/bin"), () => "1.2.3", cts.Token);

        channel.Enqueue(Envelope(new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover)));

        await WaitUntilAsync(() => runMutationCalls == 1, what: "the accept's re-mutation attempt");
        await Task.Delay(100); // give a wrongly-requeued re-dialog every chance to fire before asserting it didn't
        await Assert.That(runMutationCalls).IsEqualTo(1);
        await Assert.That(surface.Prompts.Count).IsEqualTo(1); // never re-dialogued

        cts.Cancel();
        await consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- ConsumeMutationOutcomesAsync: per-run Takeover decline memory (round-2 review R2-2) ----

    [Test]
    public async Task Decline_then_same_pair_rearrival_is_downgraded_to_one_attention_line_not_a_second_prompt() {
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(false) };
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var consumerTask = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, surface, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, cts.Token);

        var envelope = Envelope(new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover));
        channel.Enqueue(envelope);
        await WaitUntilAsync(() => surface.Prompts.Count == 1, what: "the first takeover dialog");
        await WaitUntilAsync(() => surface.StatusMessages.Count == 1, what: "the decline status line");

        channel.Enqueue(envelope); // the SAME (request, token) pair re-arrives
        await WaitUntilAsync(() => surface.AttentionMessages.Count == 1, what: "the downgraded attention presentation");
        await Assert.That(surface.Prompts.Count).IsEqualTo(1); // no second dialog
        await Assert.That(surface.AttentionMessages[0]).Contains("foreign_binary");

        cts.Cancel();
        await consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Decline_then_a_different_reason_token_still_prompts() {
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(false) };
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var consumerTask = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, surface, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, cts.Token);

        channel.Enqueue(Envelope(new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover)));
        await WaitUntilAsync(() => surface.Prompts.Count == 1, what: "the first takeover dialog");

        channel.Enqueue(Envelope(new MutationOutcome.Failed(28, "identity_mismatch", RecoverySurface.Takeover))); // a different token
        await WaitUntilAsync(() => surface.Prompts.Count == 2, what: "a fresh dialog for a different token");

        cts.Cancel();
        await consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Decline_then_a_different_request_identity_still_prompts() {
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(false) };
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var consumerTask = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, surface, NeverRunMutation, FixedTerminalPath("/usr/bin"), () => null, cts.Token);

        channel.Enqueue(new OutcomeEnvelope(Req(), new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover)));
        await WaitUntilAsync(() => surface.Prompts.Count == 1, what: "the first takeover dialog");

        // Same token, a DIFFERENT daemon identity — must still prompt.
        var otherIdentity = new MutationRequest(MutationVerb.StartVerified, "default", "https://kcap.example.com:443", "daemon-b");
        channel.Enqueue(new OutcomeEnvelope(otherIdentity, new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover)));
        await WaitUntilAsync(() => surface.Prompts.Count == 2, what: "a fresh dialog for a different identity");

        cts.Cancel();
        await consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Accept_does_not_record_decline_memory_same_pair_rearrival_still_prompts() {
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) };
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();
        var runCount = 0;
        Task<MutationOutcome> RunMutation(MutationRequest r, CancellationToken ct) {
            runCount++;
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        }

        var consumerTask = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, surface, RunMutation, FixedTerminalPath("/usr/bin"), () => null, cts.Token);

        var envelope = Envelope(new MutationOutcome.Failed(28, "foreign_binary", RecoverySurface.Takeover));
        channel.Enqueue(envelope);
        await WaitUntilAsync(() => surface.Prompts.Count == 1, what: "the first takeover dialog");
        await WaitUntilAsync(() => runCount == 1, what: "the accept re-mutation");

        channel.Enqueue(envelope); // SAME pair — accept never records memory, so this still prompts
        await WaitUntilAsync(() => surface.Prompts.Count == 2, what: "a fresh dialog since accept never records memory");

        cts.Cancel();
        await consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
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
        await using var lane = new DaemonMutationLane(Daemons.Store,
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

    // ---- round-1 review I-2: composed real-lane + real-consumer wiring ----

    // This is the test that would have caught C-2's double presentation: a REAL DaemonMutationLane
    // and a REAL ConsumeMutationOutcomesAsync loop, driving a REAL DaemonLifecycleController's
    // startup matrix through a fake executor whose install exits with a Takeover-routed coded
    // failure. Asserts exactly ONE presentation (the takeover dialog, declined -> its one status
    // line) and that the controller itself made zero direct Attention calls and wrote no OTHER
    // status line.
    [Test]
    public async Task Composed_real_lane_and_consumer_present_exactly_once_and_controller_makes_no_surface_call() {
        var channel = new OutcomeChannel();
        var client = new FakeDaemonClientService();
        var cli = new FakeKcapCli {
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
                new ServiceSnapshot("default", false, "not_installed", null, "/opt/kcap/kcapd", null, null, false, false)),
            InstallVerifiedBehavior = (_, _) => Task.FromResult(new ProcessResult(28, "", "start_gate_reason=foreign_binary", false)),
        };
        var probe = new FakeLoginShellProbe();
        using var tmp = new TempDir();
        var store = new AppStateStore(tmp.PathTo("app-state.json"));
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(false) }; // decline

        {
            await using var lane = new DaemonMutationLane(Daemons.Store,
                probe, channel, () => "/opt/kcap/bin/kcap",
                (_, _) => cli,
                _ => new NeverObservation(),
                TimeProvider.System);

            await using var controller = new DaemonLifecycleController(
                client, cli, probe, store, surface, () => Task.FromResult<string?>("default"), TimeProvider.System,
                "https://kcap.example.com:443", lane.RunAsync);

            using var consumerCts = new CancellationTokenSource();
            var consumerTask = AppUnderTest.ConsumeMutationOutcomesAsync(
                channel, surface, lane.RunAsync, probe.TerminalPathAsync, () => controller.CliVersion, consumerCts.Token);

            controller.Start();
            client.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            await WaitUntilAsync(() => surface.Prompts.Count == 1, what: "the takeover dialog");
            await Assert.That(surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindTakeover);
            await WaitUntilAsync(() => surface.StatusMessages.Count == 1, what: "the decline's one status line");
            await Assert.That(surface.AttentionMessages).IsEmpty(); // the controller made ZERO direct surface calls

            consumerCts.Cancel();
            await consumerTask.WaitAsync(TimeSpan.FromSeconds(5)); // swallowed by design — completes normally
        }
    }

    // round-2 review R2-4: the accept-variant composed test — a REAL DaemonMutationLane, a REAL
    // OutcomeChannel, and a REAL ConsumeMutationOutcomesAsync loop; no controller needed (the test
    // drives the FIRST lane.RunAsync call itself, exactly as DaemonLifecycleController or
    // DaemonClientService would). The executor's install exits 28/foreign_binary; the surface
    // fake accepts the takeover dialog; the executor's SUBSEQUENT replace call exits 0 ("returns
    // success" at the process level). Asserts the second lane request is genuinely Replace at the
    // SAME identity and that accepting never shows a second dialog (covers accept-through-the-
    // real-lane, including its own re-entry into RunAsync).
    [Test]
    public async Task Composed_accept_re_enters_the_real_lane_as_replace_with_no_second_prompt() {
        var channel = new OutcomeChannel();
        var cli = new FakeKcapCli {
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
                new ServiceSnapshot("default", false, "not_installed", null, "/opt/kcap/kcapd", null, null, false, false)),
            InstallVerifiedBehavior = (replace, _) => Task.FromResult(replace
                ? new ProcessResult(0, "", "", false)
                : new ProcessResult(28, "", "start_gate_reason=foreign_binary", false)),
        };
        var probe = new FakeLoginShellProbe();
        var surface = new FakeLifecycleSurface { ConfirmBehavior = (_, _) => Task.FromResult(true) }; // accept

        await using var lane = new DaemonMutationLane(Daemons.Store,
            probe, channel, () => "/opt/kcap/bin/kcap",
            (_, _) => cli,
            _ => new NeverObservation(),
            TimeProvider.System);

        using var consumerCts = new CancellationTokenSource();
        var consumerTask = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, surface, lane.RunAsync, probe.TerminalPathAsync, () => "1.2.3", consumerCts.Token);

        var initialRequest = new MutationRequest(MutationVerb.Install, "default", "https://kcap.example.com:443", "daemon-a");
        var firstOutcome = await lane.RunAsync(initialRequest, CancellationToken.None);
        await Assert.That(firstOutcome).IsTypeOf<MutationOutcome.Failed>();

        await WaitUntilAsync(() => surface.Prompts.Count == 1, what: "the takeover dialog");
        await WaitUntilAsync(() => cli.InstallVerifiedCallCount == 2, what: "the accept's replace call through the SAME real lane");
        await Assert.That(cli.LastInstallReplace).IsTrue();
        await Assert.That(surface.Prompts.Count).IsEqualTo(1); // no second dialog
        await Assert.That(surface.StatusMessages).IsEmpty(); // accept never writes Status

        consumerCts.Cancel();
        await consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
