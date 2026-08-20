using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// Drives the real <see cref="CodexAppServerHostedAgentRuntime"/> lifecycle against
/// <see cref="FakeCodexAppServer"/> over in-memory pipes — no child process. Covers the handshake +
/// hook-trust preflight (proceed / seed-and-restart / missing), a round settling from
/// <c>turn/completed</c>, the always-decline approval bridge, usage capture, backpressure retry, and
/// transport-death unblocking a pending round.
/// </summary>
public class CodexAppServerHostedAgentRuntimeTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    sealed class FakeProcess : IAcpProcess {
        public int  Pid       => 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  => HasExited ? 0 : null;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan? timeout = null) { HasExited = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { HasExited = true; return ValueTask.CompletedTask; }
    }

    static CodexAppServerLaunch Launch(string? model = null, string? prompt = null, string sandbox = "read-only",
            string? effort = null, string approval = "never") =>
        new(Cwd: "/tmp/wt", Model: model, Effort: effort, InitialPrompt: prompt, Sandbox: sandbox,
            Approval: approval, WritableRoots: ["/tmp/wt"], ClientVersion: "0.146.0");

    /// <summary>Builds a spawn delegate that hands each spawn a fresh fake (indexed), recording the
    /// seed passed on each call so the restart path is assertable.</summary>
    static (CodexAppServerHostedAgentRuntime Runtime, List<string?> Seeds, Func<int, FakeCodexAppServer> Fake)
            Build(Func<int, FakeCodexAppServer> fakeFor, CodexAppServerLaunch launch,
                  bool emitEnvelopes = false, bool deferFirstTurn = false,
                  Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>>? requestInteraction = null) {
        var seeds  = new List<string?>();
        var fakes  = new List<FakeCodexAppServer>();
        var index  = 0;

        CodexAppServerSpawn spawn = (seed, _) => {
            seeds.Add(seed);
            var fake = fakeFor(index++);
            fakes.Add(fake);
            var conn = fake.ConnectClient();
            return Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((conn, new FakeProcess()));
        };

        var runtime = new CodexAppServerHostedAgentRuntime(
            spawn, launch, clock: null, NullLogger.Instance,
            emitEnvelopeTranscript: emitEnvelopes, deferFirstTurn: deferFirstTurn,
            agentId: requestInteraction is null ? null : "agent-1",
            requestInteraction: requestInteraction,
            approvalTimeout: TimeSpan.FromSeconds(5));
        return (runtime, seeds, i => fakes[i]);
    }

    static List<AcpEventEnvelope> DrainAvailable(CodexAppServerHostedAgentRuntime runtime) {
        var list = new List<AcpEventEnvelope>();
        while (runtime.Envelopes.TryRead(out var e)) list.Add(e);
        return list;
    }

    [Test]
    public async Task Handshake_starts_thread_reports_model_and_opts_out_of_deltas() {
        var fake = new FakeCodexAppServer { Model = "gpt-5.3-codex" };
        var (runtime, _, _) = Build(_ => fake, Launch());

        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(runtime.ThreadId).IsEqualTo("thread-abc");
        await Assert.That(runtime.ResolvedModel).IsEqualTo("gpt-5.3-codex");
        await Assert.That(fake.ReceivedMethods).Contains("initialize");
        await Assert.That(fake.ReceivedMethods).Contains("hooks/list");
        await Assert.That(fake.ReceivedMethods).Contains("thread/start");
        await Assert.That(fake.InitializeOptOuts).Contains("item/agentMessage/delta");
        await Assert.That(fake.LastThreadStartSandbox).IsEqualTo("read-only");

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Envelope_transcript_emits_a_token_usage_delta_through_the_forward_buffer() {
        // Gate ON: the real HandleNotification path feeds the mapper + forward buffer, so a turn's usage
        // notification surfaces as a token_usage envelope on IAcpTranscriptSource.Envelopes.
        var fake = new FakeCodexAppServer { Model = "gpt-5.3-codex", EmitUsageOnTurn = (input: 120, output: 40, total: 160) };
        var (runtime, _, _) = Build(_ => fake, Launch(), emitEnvelopes: true);
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        // With the transcript on, the mapper needs the delta streams, so initialize must opt out of none
        // (otherwise the app-server would never send the ephemeral notifications the gate is meant to enable).
        await Assert.That(fake.InitializeOptOuts).IsEmpty();

        await runtime.SendUserInputAsync("go").WaitAsync(HangGuard);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        // The read loop processes usage BEFORE turn/completed (which unblocked the wait), so it is buffered.
        var usage = DrainAvailable(runtime).Single(e => e.Kind == AcpEventKind.TokenUsage);
        await Assert.That(usage.UsageInputTokens).IsEqualTo(120L);
        await Assert.That(usage.UsageOutputTokens).IsEqualTo(40L);
        await Assert.That(usage.Model).IsEqualTo("gpt-5.3-codex");

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Envelope_transcript_is_dormant_when_the_gate_is_off() {
        // Default (reviewer path): the notification pump never feeds the buffer, so Envelopes stays empty
        // even after a turn with usage — the shipped reviewer control-plane behavior is byte-unchanged.
        var fake = new FakeCodexAppServer { EmitUsageOnTurn = (input: 120, output: 40, total: 160) };
        var (runtime, _, _) = Build(_ => fake, Launch()); // emitEnvelopes defaults false
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await runtime.SendUserInputAsync("go").WaitAsync(HangGuard);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(DrainAvailable(runtime)).IsEmpty();

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Deferred_first_turn_holds_the_initial_prompt_until_BeginFirstTurn() {
        // The load-bearing ordering: with deferral on, StartAsync must establish the thread but leave NO
        // turn/start behind (a hook could otherwise fire before the source claim commits).
        var fake = new FakeCodexAppServer();
        var (runtime, _, _) = Build(_ => fake, Launch(prompt: "review this"), deferFirstTurn: true);

        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(runtime.RequiresSourceClaimBeforeFirstTurn).IsTrue();
        await Assert.That(runtime.ThreadId).IsEqualTo("thread-abc");
        await Assert.That(fake.ReceivedMethods).DoesNotContain("turn/start");

        await runtime.BeginFirstTurnAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(fake.ReceivedMethods).Contains("turn/start");

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Cancelled_BeginFirstTurn_does_not_dispatch_the_held_turn() {
        // A launch cancelled during the source claim must not release the held first turn — BeginFirstTurn
        // observes the token BEFORE unsealing, so turn/start never leaves for a finalizing agent.
        var fake = new FakeCodexAppServer();
        var (runtime, _, _) = Build(_ => fake, Launch(prompt: "review this"), deferFirstTurn: true);

        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);
        await Assert.That(fake.ReceivedMethods).DoesNotContain("turn/start");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await runtime.BeginFirstTurnAsync(cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(fake.ReceivedMethods).DoesNotContain("turn/start"); // never unsealed ⇒ never dispatched

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Single_phase_launch_dispatches_the_initial_prompt_at_start() {
        var fake = new FakeCodexAppServer();
        var (runtime, _, _) = Build(_ => fake, Launch(prompt: "review this")); // deferFirstTurn defaults false

        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(runtime.RequiresSourceClaimBeforeFirstTurn).IsFalse();
        await Assert.That(fake.ReceivedMethods).Contains("turn/start"); // no deferral ⇒ the prompt drives the first turn at start

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Round_settles_from_turn_completed_and_pins_never_approval() {
        var fake = new FakeCodexAppServer();
        var (runtime, _, _) = Build(_ => fake, Launch());
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await runtime.SendUserInputAsync("please review").WaitAsync(HangGuard);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(fake.ReceivedMethods).Contains("turn/start");
        await Assert.That(fake.LastTurnApprovalPolicy).IsEqualTo("never");

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Input_on_an_active_turn_is_steered_not_restarted() {
        var fake = new FakeCodexAppServer { HoldTurnOpen = true };
        var (runtime, _, _) = Build(_ => fake, Launch());
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await runtime.SendUserInputAsync("first").WaitAsync(HangGuard);  // turn/start — turn stays active
        await runtime.SendUserInputAsync("second").WaitAsync(HangGuard); // steered onto the active turn

        await Assert.That(fake.ReceivedMethods).Contains("turn/steer");
        await Assert.That(fake.LastSteerExpectedTurnId).IsEqualTo("turn-1");
        await Assert.That(fake.LastSteerText).IsEqualTo("second");
        await Assert.That(fake.ReceivedMethods.Count(m => m == "turn/start")).IsEqualTo(1); // NOT restarted

        await fake.CompleteHeldTurnAsync();
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);
        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Initial_prompt_fires_the_first_turn_during_start() {
        var fake = new FakeCodexAppServer();
        var (runtime, _, _) = Build(_ => fake, Launch(prompt: "kick off the review"));

        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(fake.ReceivedMethods).Contains("turn/start");
        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Untrusted_kcap_hook_seeds_and_restarts_the_child() {
        var untrusted = new FakeCodexAppServer {
            HooksData = FakeCodexAppServer.HookData([
                ("sessionStart", "kcap hook --codex", "untrusted", "sha256:aa"),
                ("stop", "kcap hook --codex", "trusted", "sha256:b"),
                ("permissionRequest", "kcap hook --codex", "trusted", "sha256:c"),
            ]),
        };
        var trusted = new FakeCodexAppServer(); // second spawn sees a fully trusted set

        var (runtime, seeds, _) = Build(i => i == 0 ? untrusted : trusted, Launch());
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        // Two spawns: the first with no seed, the second carrying a hooks.state override.
        await Assert.That(seeds.Count).IsEqualTo(2);
        await Assert.That(seeds[0]).IsNull();
        await Assert.That(seeds[1]).IsNotNull();
        await Assert.That(seeds[1]!).StartsWith("hooks.state={");
        await Assert.That(runtime.ThreadId).IsEqualTo("thread-abc");

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Missing_critical_hook_fails_the_launch_closed() {
        var fake = new FakeCodexAppServer {
            HooksData = FakeCodexAppServer.HookData([
                ("sessionStart", "kcap hook --codex", "trusted", "sha256:a"),
                // no Stop, no PermissionRequest
            ]),
        };
        var (runtime, _, _) = Build(_ => fake, Launch());

        await Assert.ThrowsAsync<CodexHooksNotInstalledException>(
            () => runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard));

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Unexpected_approval_request_is_declined_on_the_wire() {
        var fake = new FakeCodexAppServer { InjectApprovalDuringTurn = true };
        var (runtime, _, _) = Build(_ => fake, Launch());
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await runtime.SendUserInputAsync("review").WaitAsync(HangGuard);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        // The client answered the approval with a valid decline result, never a JSON-RPC error.
        await Assert.That(fake.ApprovalResponse).IsNotNull();
        await Assert.That(fake.ApprovalResponse!.Value.GetProperty("decision").GetString()).IsEqualTo("decline");

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Interactive_approval_is_forwarded_and_the_user_decision_is_returned_on_the_wire() {
        // An interactive launch (approvalPolicy != never) with a requestInteraction delegate routes the
        // server's approval request to the user and answers with their mapped decision — NOT the reviewer
        // decline. This proves the OnServerRequest wiring reaches the bridge end to end.
        var fake = new FakeCodexAppServer { InjectApprovalDuringTurn = true };
        var (runtime, _, _) = Build(_ => fake, Launch(approval: "on-request"),
            requestInteraction: (_, _) => Task.FromResult(
                new AcpInteractionDecision("allow", "accept", "Allow", null, null, null)));
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await runtime.SendUserInputAsync("do it").WaitAsync(HangGuard);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(fake.ApprovalResponse).IsNotNull();
        await Assert.That(fake.ApprovalResponse!.Value.GetProperty("decision").GetString()).IsEqualTo("accept");

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Token_usage_notification_is_captured() {
        var fake = new FakeCodexAppServer { EmitUsageOnTurn = (input: 120, output: 40, total: 160) };
        var (runtime, _, _) = Build(_ => fake, Launch());
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await runtime.SendUserInputAsync("review").WaitAsync(HangGuard);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(runtime.Usage).IsNotNull();
        await Assert.That(runtime.Usage!.Value.TotalTokens).IsEqualTo(160L);
        await Assert.That(runtime.Usage!.Value.InputTokens).IsEqualTo(120L);

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Turn_start_backpressure_is_retried_until_it_succeeds() {
        var fake = new FakeCodexAppServer { Fail32001TimesOnTurnStart = 2 };
        var (runtime, _, _) = Build(_ => fake, Launch());
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        // Two -32001 rejections then success — the round still settles rather than throwing.
        await runtime.SendUserInputAsync("review").WaitAsync(HangGuard);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(fake.ReceivedMethods.Count(m => m == "turn/start")).IsEqualTo(3);
        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Failed_turn_status_still_settles_the_round() {
        var fake = new FakeCodexAppServer { TurnStatus = "failed" };
        var (runtime, _, _) = Build(_ => fake, Launch());
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await runtime.SendUserInputAsync("review").WaitAsync(HangGuard);
        // A failed turn is still a completed round — WaitForTurnIdle must return, not hang.
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Local_raw_input_is_unsupported() {
        var fake = new FakeCodexAppServer();
        var (runtime, _, _) = Build(_ => fake, Launch());
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.ThrowsAsync<NotSupportedException>(() => runtime.SendRawInputAsync([1, 2, 3]));
        await Assert.That(runtime.EmitsTerminalOutput).IsFalse();

        await runtime.DisposeAsync();
    }

    // The orchestrator treats ReadOutputAsync ending as the finalize trigger, so it must NOT complete
    // until the runtime is terminal — otherwise every reviewer is killed seconds after launch.
    [Test]
    public async Task ReadOutput_does_not_complete_until_the_runtime_is_terminal() {
        var fake = new FakeCodexAppServer();
        var (runtime, _, _) = Build(_ => fake, Launch());
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await using var e = runtime.ReadOutputAsync().GetAsyncEnumerator();
        var moveNext = e.MoveNextAsync().AsTask();

        // Still live: the stream must not end while the runtime is running.
        var early = await Task.WhenAny(moveNext, Task.Delay(300));
        await Assert.That(early).IsNotSameReferenceAs(moveNext);

        await runtime.DisposeAsync();
        // Terminal now: the stream ends (no bytes ever, EmitsTerminalOutput=false).
        await Assert.That(await moveNext.WaitAsync(HangGuard)).IsFalse();
    }

    // The hook-trust seed-and-restart tears down child 1; its read-loop end must NOT trip the
    // whole-runtime terminal signal, or a round on child 2 would report idle immediately.
    [Test]
    public async Task A_round_after_the_hook_seed_restart_still_settles_from_turn_completed() {
        var untrusted = new FakeCodexAppServer {
            HooksData = FakeCodexAppServer.HookData([
                ("sessionStart", "kcap hook --codex", "untrusted", "sha256:aa"),
                ("stop", "kcap hook --codex", "trusted", "sha256:b"),
                ("permissionRequest", "kcap hook --codex", "trusted", "sha256:c"),
            ]),
        };
        var trusted = new FakeCodexAppServer();
        var (runtime, _, fakeAt) = Build(i => i == 0 ? untrusted : trusted, Launch());
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await runtime.SendUserInputAsync("review").WaitAsync(HangGuard);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        // The turn ran against child 2 (the trusted respawn), and the round genuinely settled.
        await Assert.That(fakeAt(1).ReceivedMethods).Contains("turn/start");
        await runtime.DisposeAsync();
    }

    [Test]
    public async Task Requested_effort_is_mapped_and_passed_on_turn_start() {
        var fake = new FakeCodexAppServer();
        var (runtime, _, _) = Build(_ => fake, Launch(effort: "max"));
        await runtime.StartAsync(CancellationToken.None).WaitAsync(HangGuard);

        await runtime.SendUserInputAsync("review").WaitAsync(HangGuard);
        await runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        // "max" maps to "xhigh", mirroring the PTY launcher.
        await Assert.That(fake.LastTurnEffort).IsEqualTo("xhigh");
        await runtime.DisposeAsync();
    }

    // The daemon reports "app-server" only for the app-server codex runtime.
    [Test]
    public async Task AgentInstance_reports_app_server_transport_for_the_app_server_runtime() {
        var (runtime, _, _) = Build(_ => new FakeCodexAppServer(), Launch());
        var agent = new AgentInstance(
            "a1", null, null, null, "/r", "codex", runtime,
            new WorktreeInfo("/r", "", "/r", IsStandalone: true), new CancellationTokenSource());

        await Assert.That(agent.RuntimeTransport).IsEqualTo(CodexTransportDecision.AppServer);
        await runtime.DisposeAsync();
    }
}
