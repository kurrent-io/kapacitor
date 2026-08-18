using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
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

    static CodexAppServerLaunch Launch(string? model = null, string? prompt = null, string sandbox = "read-only") =>
        new(Cwd: "/tmp/wt", Model: model, InitialPrompt: prompt, Sandbox: sandbox,
            Approval: "never", WritableRoots: ["/tmp/wt"], ClientVersion: "0.146.0");

    /// <summary>Builds a spawn delegate that hands each spawn a fresh fake (indexed), recording the
    /// seed passed on each call so the restart path is assertable.</summary>
    static (CodexAppServerHostedAgentRuntime Runtime, List<string?> Seeds, Func<int, FakeCodexAppServer> Fake)
            Build(Func<int, FakeCodexAppServer> fakeFor, CodexAppServerLaunch launch) {
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
            spawn, launch, clock: null, NullLogger.Instance);
        return (runtime, seeds, i => fakes[i]);
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
        await Assert.That(fake.LastThreadStartSandboxType).IsEqualTo("readOnly");

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
}
