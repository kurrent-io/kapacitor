using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Acp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Round-4 review finding 3: proves <see cref="AcpHostedAgentRuntimeFactory"/> — constructed for
/// real, driven through its REAL <see cref="AcpHostedAgentRuntimeFactory.StartAsync"/> — actually
/// wires the ACP interaction bridge into the runtime it produces, by observing an inbound
/// <c>session/request_permission</c> genuinely dispatch to the injected <c>requestInteraction</c>
/// delegate. Does NOT spawn a real <c>cursor-agent acp</c> process (unavailable/non-portable in
/// CI, `.github/workflows/ci.yml`'s `ubuntu-latest`/`windows-latest` matrix) — the factory's
/// process-spawning is swapped out via its <c>connectionSource</c> constructor seam for one backed
/// by <see cref="FakeAcpAgent"/>'s existing in-memory pipe streams, the same fake this project
/// already uses for <c>AcpHostedAgentRuntimeTests</c>/<c>AcpHostedAgentRuntimePermissionTests</c>.
/// A regression that left <c>StartAsync</c> passing <c>requestInteraction: null</c> (reverting to
/// the runtime's original default-decline posture) would make this test's <c>session/request_permission</c>
/// resolve with a JSON-RPC "Method not found" error instead of the well-formed <c>cancelled</c>
/// outcome asserted below — i.e. this test FAILS on that regression, unlike the pre-round-4 test it
/// replaces.
/// </summary>
public class AcpHostedAgentRuntimeFactoryTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int  Pid       { get; init; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }
        public void SignalExited(int exitCode = 0) { HasExited = true; ExitCode = exitCode; _exited.TrySetResult(); }
        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;
        public Task TerminateAsync(TimeSpan? timeout = null) { SignalExited(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Records whether <see cref="ServerConnection.RequestAcpInteractionAsync"/> was actually
    /// invoked BY THE RUNTIME THE FACTORY PRODUCED (not by the test calling it directly) — a real
    /// (non-connecting) <see cref="ServerConnection"/> subclass, matching the established
    /// <c>CaptureServerConnection</c>-style pattern used elsewhere in this test project (e.g.
    /// <c>AgentOrchestratorVendorTests.cs</c>) rather than a mocking framework, since
    /// <see cref="ServerConnection"/> is not an interface.
    /// </summary>
    sealed class CaptureServerConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance
        ) {
        public bool RequestAcpInteractionAsyncCalled { get; private set; }
        public AcpInteractionRequest? LastRequest     { get; private set; }

        public override Task<AcpInteractionDecision> RequestAcpInteractionAsync(AcpInteractionRequest request, CancellationToken ct = default) {
            RequestAcpInteractionAsyncCalled = true;
            LastRequest                      = request;

            return Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null));
        }
    }

    static RuntimeStartContext MakeContext(string agentId) => new(
        AgentId: agentId, Vendor: "cursor", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/worktree", Branch: "branch-name", SourceRepo: "/repo"), Prompt: "",
        Model: "default", Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: false, Review: null,
        Cols: 80, Rows: 24, ServerUrl: null, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap");

    [Test]
    public async Task StartAsync_WiresRequestInteractionDelegate_DispatchesInboundPermissionRequestToTheBridge() {
        var fake       = new FakeAcpAgent();
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "cursor-agent" }, // never actually spawned — connectionSource below bypasses Process.Start
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await factory.StartAsync(MakeContext("agent-1"), cts.Token).WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Run ls"}""",
            optionsJson: """[{"optionId":"allow-once","name":"Allow","kind":"allow_once"}]""");

        await started.Runtime.SendUserInputAsync("run ls").WaitAsync(HangGuard);

        // The factory-produced runtime dispatched the inbound session/request_permission to the
        // bridge, which called connection.RequestAcpInteractionAsync — proving requestInteraction
        // was genuinely wired (not left null) by StartAsync, observed through the REAL runtime the
        // REAL factory produced, not a direct delegate invocation.
        var deadline = DateTime.UtcNow + HangGuard;
        while (!connection.RequestAcpInteractionAsyncCalled && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(connection.RequestAcpInteractionAsyncCalled).IsTrue();
        await Assert.That(connection.LastRequest?.AgentId).IsEqualTo("agent-1");
        await Assert.That(connection.LastRequest?.Kind).IsEqualTo("permission");

        var responseDeadline = DateTime.UtcNow + HangGuard;
        while (fake.LastServerRequestResponse is null && DateTime.UtcNow < responseDeadline)
            await Task.Delay(10);

        // connection.RequestAcpInteractionAsync above returns a "cancel" decision — the bridge
        // (Task B3) must map that to the well-formed ACP "cancelled" outcome, proving the FULL
        // chain (factory → runtime → bridge → injected ServerConnection → back to the wire) works,
        // not just that SOME delegate got called.
        await Assert.That(fake.LastServerRequestResponse).IsNotNull();
        await Assert.That(fake.LastServerRequestResponse!.Value.GetProperty("outcome").GetProperty("outcome").GetString()).IsEqualTo("cancelled");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>
    /// Model-precedence gap: <c>ctx.Model</c> (the launch's own model override) must take precedence over
    /// <c>DaemonConfig.CursorModel</c> (the daemon-wide family-prefix default) — proves the full
    /// chain (factory merges the two, runtime resolves against `session/new`'s `availableModels`,
    /// sends `session/set_config_option`) picks the PER-LAUNCH model, not the daemon default.
    /// </summary>
    [Test]
    public async Task StartAsync_CtxModelOverridesConfigCursorModel_AndSendsSetConfigOptionForIt() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId,
            currentModelId: "composer-2.5[fast=true]",
            availableModels: [
                ("composer-2.5[fast=true]", "composer-2.5"),
                ("claude-sonnet-4-5[thinking=true,context=200k]", "claude-sonnet-4-5"),
                ("claude-opus-4-8[thinking=true]", "claude-opus-4-8"),
            ]));
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "cursor-agent", CursorModel = "claude-sonnet-4-5" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx = MakeContext("agent-1") with { Model = "claude-opus-4-8" };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (!fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var setConfigCall = fake.ReceivedCalls.Single(c => c.Method == "session/set_config_option");
        await Assert.That(setConfigCall.Params!.Value.GetProperty("value").GetString()).IsEqualTo("claude-opus-4-8[thinking=true]");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>
    /// Negative control proving this test suite WOULD catch the round-4 regression it replaces: a
    /// factory built with <c>connectionSource</c> returning a runtime whose <c>requestInteraction</c>
    /// is deliberately left <see langword="null"/> (simulating the pre-Finding-4 defect) answers the
    /// SAME inbound request with a JSON-RPC "Method not found" error, not a "cancelled" outcome —
    /// demonstrating this test file's assertions are sensitive to the exact bug Finding 4 fixed and
    /// Finding 3 makes verifiable end-to-end.
    /// </summary>
    [Test]
    public async Task StartAsync_IfRequestInteractionWereNull_PermissionRequestWouldGetMethodNotFound() {
        var fake = new FakeAcpAgent();
        var conn = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger.Instance);
        var runtime = new AcpHostedAgentRuntime(conn, new FakeAcpProcess(), NullLogger.Instance); // no requestInteraction — default behavior

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        await runtime.StartAsync("/abs/worktree", "", cts.Token).WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Run ls"}""",
            optionsJson: """[{"optionId":"allow-once","name":"Allow","kind":"allow_once"}]""");

        await runtime.SendUserInputAsync("run ls").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.LastServerRequestError is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(fake.LastServerRequestError).IsNotNull();
        await Assert.That(fake.LastServerRequestError!.Value.GetProperty("code").GetInt32()).IsEqualTo(-32601);

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    // ── Payload-free "ACP hosted agent launch" Info logging ────────────────────────────────────

    /// <summary>Records every log call across every category (one instance shared by every
    /// <c>CreateLogger&lt;T&gt;()</c> call) — mirrors <c>AcpTranscriptAggregationTests.CaptureLogger</c>'s
    /// established pattern, wrapped in a minimal <see cref="ILoggerFactory"/> so it can be handed to
    /// the factory's real constructor.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    sealed class CaptureLoggerFactory : ILoggerFactory {
        public readonly CaptureLogger Logger = new();

        public ILogger CreateLogger(string categoryName) => Logger;
        public void    AddProvider(ILoggerProvider provider) { }
        public void    Dispose() { }
    }

    [Test]
    public async Task StartAsync_LogsAcpHostedAgentLaunch_WithAgentIdVendorAndCwd() {
        var fake           = new FakeAcpAgent();
        var connection     = new CaptureServerConnection();
        var loggerFactory  = new CaptureLoggerFactory();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "cursor-agent" },
            loggerFactory: loggerFactory,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx     = MakeContext("agent-launch-log") with { Worktree = new WorktreeInfo(Path: "/abs/some-worktree", Branch: "b", SourceRepo: "/repo") };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var infoEntries = loggerFactory.Logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        await Assert.That(infoEntries).Contains(e =>
            e.Message.Contains("ACP hosted agent launch")
            && e.Message.Contains("agent-launch-log")
            && e.Message.Contains("cursor")
            && e.Message.Contains("/abs/some-worktree"));

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    // ── Test plan item 1: Cursor pin test (spec-review Finding 4) ──────────────────────────

    /// <summary>
    /// (a) proves the actual PRODUCTION <see cref="AcpHostedAgentRuntimeFactory.BuildProcessStartInfo"/>
    /// shape for the real Cursor descriptor — no <c>connectionSource</c>, no fake, no
    /// <c>StartAsync</c> — this is the only place in the suite that can observe it, since every
    /// other test replaces process-spawning entirely via <c>connectionSource</c>. (b) drives a full
    /// <c>StartAsync</c> against <see cref="FakeAcpAgent"/> and asserts the exact
    /// <c>initialize</c>/<c>session/new</c> frames are byte-identical to today's — in particular
    /// <c>session/new</c>'s <c>mcpServers</c> is <c>[]</c> (an empty array, not omitted, not
    /// populated) when <c>ctx.McpServers</c> is left at its default <see langword="null"/>. Together
    /// these are the primary regression guard for the whole refactor.
    /// </summary>
    [Test]
    public async Task StartAsync_ForCursorDescriptor_SpawnsExactSameArgvAndHandshakeAsBeforeAI1401() {
        // (a) — pure BuildProcessStartInfo assertion.
        var config = new DaemonConfig { CursorPath = "/usr/local/bin/cursor-agent" };
        var ctx    = MakeContext("agent-1");

        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(AcpVendorDescriptors.Cursor, config, ctx);

        await Assert.That(psi.FileName).IsEqualTo(config.CursorPath);
        await Assert.That(psi.ArgumentList.SequenceEqual(["acp"])).IsTrue();
        await Assert.That(psi.WorkingDirectory).IsEqualTo(ctx.Worktree.Path);

        // (b) — full StartAsync against FakeAcpAgent; assert the exact initialize/session/new frames.
        var fake       = new FakeAcpAgent();
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "cursor-agent" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.ReceivedCalls.Count(c => c.Method is "initialize" or "session/new") < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var initializeCall = fake.ReceivedCalls.Single(c => c.Method == "initialize");
        await Assert.That(initializeCall.Params!.Value.GetProperty("protocolVersion").GetInt32()).IsEqualTo(1);
        await Assert.That(initializeCall.Params!.Value.GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean()).IsFalse();
        await Assert.That(initializeCall.Params!.Value.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean()).IsFalse();

        var sessionNewCall = fake.ReceivedCalls.Single(c => c.Method == "session/new");
        await Assert.That(sessionNewCall.Params!.Value.GetProperty("cwd").GetString()).IsEqualTo(ctx.Worktree.Path);
        await Assert.That(sessionNewCall.Params!.Value.GetProperty("mcpServers").GetRawText()).IsEqualTo("[]");

        await Assert.That(started.Runtime.Vendor).IsEqualTo("cursor");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    // ── Test plan item 5: descriptor-driven spawn args via BuildProcessStartInfo ──────────

    /// <summary>Second, test-only descriptor — not <see cref="AcpVendorDescriptors.Cursor"/> — used
    /// by test plan items 5 and 6. <c>SupportsMcpServers</c> is parameterized since item 6(c) needs
    /// it <see langword="false"/> while items 6(a)/6(b) need it <see langword="true"/>; every other
    /// field is identical across both.</summary>
    static AcpVendorDescriptor SyntheticDescriptor(bool supportsMcpServers) => new(
        Vendor:              "test-acp-vendor",
        ResolveBinaryPath:   _ => "test-acp-vendor-cli",
        ResolveDefaultModel: _ => null,
        Argv:                ["acp", "--flag-a"],
        UnattendedTrustArgv: ["--trust"],
        SupportsUnattended:  true,
        ModelSelector:       NoOpModelSelector.Instance,
        SupportsMcpServers:  supportsMcpServers,
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.AutoApprove
    );

    static AcpVendorDescriptor NonUnattendedDescriptor() => new(
        Vendor:              "interactive-only",
        ResolveBinaryPath:   _ => "interactive-only",
        ResolveDefaultModel: _ => null,
        Argv:                ["acp"],
        UnattendedTrustArgv: [],
        SupportsUnattended:  false,
        ModelSelector:       NoOpModelSelector.Instance,
        SupportsMcpServers:  true);

    /// <summary>
    /// Exercises the generic trust-argv seam independently of production vendors. Cursor has no
    /// trust-at-spawn argv, while Copilot uses concrete trust flags and an alternate MCP transport.
    /// </summary>
    [Test]
    public async Task BuildProcessStartInfo_DescriptorDriven_AppendsTrustArgvOnlyForReviewFlow() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: false);
        var config     = new DaemonConfig();

        var interactivePsi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            descriptor, config, MakeContext("agent-1") with { IsReviewFlow = false });
        var reviewFlowPsi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            descriptor, config, MakeContext("agent-1") with { IsReviewFlow = true });

        await Assert.That(interactivePsi.ArgumentList.SequenceEqual(["acp", "--flag-a"])).IsTrue();
        await Assert.That(reviewFlowPsi.ArgumentList.SequenceEqual(["acp", "--flag-a", "--trust"])).IsTrue();
    }

    [Test]
    public async Task BuildProcessStartInfo_CursorReviewFlow_UsesCursorZeroPromptFlags() {
        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Cursor,
            new DaemonConfig { CursorPath = "/opt/cursor/cursor-agent" },
            ReviewContext());

        await Assert.That(psi.ArgumentList.SequenceEqual([
            "acp", "--force", "--approve-mcps", "--trust"
        ])).IsTrue();
    }

    /// <summary>Qodo finding 3: defense-in-depth — even though the orchestrator's
    /// <c>UnattendedLaunchPolicy</c> is expected to reject a review-flow launch for a vendor that
    /// doesn't support it before the factory ever runs, <c>BuildProcessStartInfo</c> refuses to
    /// build review-flow argv for a <c>SupportsUnattended: false</c> descriptor rather than
    /// trusting that gate alone.</summary>
    [Test]
    public async Task BuildProcessStartInfo_Throws_ForReviewFlow_WhenDescriptorDoesNotSupportUnattended() {
        var descriptor = NonUnattendedDescriptor();
        var config     = new DaemonConfig();

        await Assert.That(() => AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            descriptor, config, MakeContext("agent-1") with { IsReviewFlow = true }
        )).Throws<InvalidOperationException>();
    }

    // ── Test plan item 6: mcpServers gating and wire shape ─────────────────────────────────

    static async Task<HostedRuntimeStart> RunSyntheticStartAsync(
            AcpVendorDescriptor descriptor, FakeAcpAgent fake, RuntimeStartContext ctx, CancellationToken ct) {
        var connection = new CaptureServerConnection();
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: descriptor,
            config: new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        return await factory.StartAsync(ctx, ct).WaitAsync(HangGuard, ct);
    }

    static async Task<string> WaitForSessionNewMcpServersJsonAsync(FakeAcpAgent fake) {
        var deadline = DateTime.UtcNow + HangGuard;
        while (!fake.ReceivedCalls.Any(c => c.Method == "session/new") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        return fake.ReceivedCalls.Single(c => c.Method == "session/new").Params!.Value.GetProperty("mcpServers").GetRawText();
    }

    [Test]
    public async Task StartAsync_SupportsMcpServersTrue_PopulatedContext_ForwardsServerVerbatim() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: true);
        var fake        = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        AcpMcpServerSpec[] mcpServers = [
            new AcpMcpServerSpec(Name: "fs", Command: "npx",
                Args: ["-y", "@modelcontextprotocol/server-filesystem"],
                Env: [new AcpMcpServerEnvVar("FOO", "bar")])
        ];
        var ctx = MakeContext("agent-1") with { McpServers = mcpServers };

        var started = await RunSyntheticStartAsync(descriptor, fake, ctx, cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(mcpServersJson).IsEqualTo(
            """[{"name":"fs","command":"npx","args":["-y","@modelcontextprotocol/server-filesystem"],"env":[{"name":"FOO","value":"bar"}]}]""");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>The exact regression Finding 1 flagged: an empty <c>Env</c> must still serialize as
    /// <c>"env":[]</c>, NOT an omitted key and NOT <c>"env":null</c>.</summary>
    [Test]
    public async Task StartAsync_SupportsMcpServersTrue_EmptyEnv_SerializesAsEmptyArray_NotOmittedNotNull() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: true);
        var fake        = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        AcpMcpServerSpec[] mcpServers = [
            new AcpMcpServerSpec(Name: "fs", Command: "npx", Args: ["-y", "server-filesystem"], Env: [])
        ];
        var ctx = MakeContext("agent-1") with { McpServers = mcpServers };

        var started = await RunSyntheticStartAsync(descriptor, fake, ctx, cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(mcpServersJson).IsEqualTo("""[{"name":"fs","command":"npx","args":["-y","server-filesystem"],"env":[]}]""");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>Proves the DESCRIPTOR flag — not just an unpopulated context — is what gates
    /// forwarding: even with a populated <c>ctx.McpServers</c>, <c>SupportsMcpServers: false</c>
    /// still sends <c>mcpServers: []</c> on the wire.</summary>
    [Test]
    public async Task StartAsync_SupportsMcpServersFalse_PopulatedContext_StillSendsEmptyArray() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: false);
        var fake        = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        AcpMcpServerSpec[] mcpServers = [
            new AcpMcpServerSpec(Name: "fs", Command: "npx",
                Args: ["-y", "@modelcontextprotocol/server-filesystem"],
                Env: [new AcpMcpServerEnvVar("FOO", "bar")])
        ];
        var ctx = MakeContext("agent-1") with { McpServers = mcpServers };

        var started = await RunSyntheticStartAsync(descriptor, fake, ctx, cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(mcpServersJson).IsEqualTo("[]");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    // ── Test plan item 11: factory-selector integration + frame ordering ──────────────────

    static async Task<IReadOnlyList<(string Method, System.Text.Json.JsonElement? Params)>> WaitForCallCountAsync(FakeAcpAgent fake, int minCount) {
        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.ReceivedCalls.Count < minCount && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        return fake.ReceivedCalls;
    }

    static readonly (string ModelId, string Name)[] TeamAvailableModels = [
        ("default[]", "default"),
        ("composer-2.5[fast=true]", "composer-2.5"),
        ("claude-sonnet-4-5[thinking=true,context=200k]", "claude-sonnet-4-5"),
        ("claude-opus-4-8[thinking=true]", "claude-opus-4-8"),
    ];

    /// <summary>(a) An explicit <c>ctx.Model</c> that resolves → order is <c>initialize</c>,
    /// <c>session/new</c>, <c>session/set_config_option</c>, <c>session/prompt</c>, and the
    /// started runtime's <c>Vendor == "cursor"</c>.</summary>
    [Test]
    public async Task StartAsync_ExplicitResolvableModel_FrameOrderIsInitializeNewSetConfigPrompt() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorModel = "claude-sonnet-4-5" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx     = MakeContext("agent-1") with { Model = "claude-opus-4-8", Prompt = "do the thing" };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(fake, minCount: 4);
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(calls[0].Method).IsEqualTo("initialize");
        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[2].Method).IsEqualTo("session/set_config_option");
        await Assert.That(calls[2].Params!.Value.GetProperty("value").GetString()).IsEqualTo("claude-opus-4-8[thinking=true]");
        await Assert.That(calls[3].Method).IsEqualTo("session/prompt");
        await Assert.That(started.Runtime.Vendor).IsEqualTo("cursor");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>(b) <c>ctx.Model: "default"</c> (the UI's "no override" sentinel) → resolves to
    /// <c>config.CursorModel</c> against <c>session/new</c>'s <c>availableModels</c>, same
    /// four-call order — the sentinel still resolves TO a model (the configured default), not a
    /// caller override.</summary>
    [Test]
    public async Task StartAsync_DefaultSentinelModel_ResolvesToConfigCursorModel_FrameOrderIsInitializeNewSetConfigPrompt() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorModel = "claude-sonnet-4-5" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx     = MakeContext("agent-1") with { Model = "default", Prompt = "do the thing" };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(fake, minCount: 4);
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(calls[0].Method).IsEqualTo("initialize");
        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[2].Method).IsEqualTo("session/set_config_option");
        await Assert.That(calls[2].Params!.Value.GetProperty("value").GetString()).IsEqualTo("claude-sonnet-4-5[thinking=true,context=200k]");
        await Assert.That(calls[3].Method).IsEqualTo("session/prompt");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>(c) A requested model NOT present in <c>availableModels</c> → order is
    /// <c>initialize</c>, <c>session/new</c>, <c>session/prompt</c> only — NO
    /// <c>session/set_config_option</c> call, proving an unresolvable model never even attempts
    /// the RPC.</summary>
    [Test]
    public async Task StartAsync_UnresolvableModel_FrameOrderSkipsSetConfigOption() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorModel = "claude-sonnet-4-5" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx     = MakeContext("agent-1") with { Model = "totally-unknown-model", Prompt = "do the thing" };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(fake, minCount: 3);
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(calls[0].Method).IsEqualTo("initialize");
        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[2].Method).IsEqualTo("session/prompt");
        await Assert.That(calls.Any(c => c.Method == "session/set_config_option")).IsFalse();

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>(d) A resolvable model but <c>fake.FailNextSetConfigOption()</c> → the full
    /// four-call order (the RPC IS attempted and fails) and <c>session/prompt</c> still fires
    /// afterward with no exception — the integration-level counterpart to test plan item 10, now
    /// proving the FACTORY-produced runtime (not a hand-built one) behaves the same way.</summary>
    [Test]
    public async Task StartAsync_SetConfigOptionRpcError_FrameOrderStillReachesPrompt_NoException() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        fake.FailNextSetConfigOption();
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorModel = "claude-sonnet-4-5" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx     = MakeContext("agent-1") with { Model = "claude-opus-4-8", Prompt = "do the thing" };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(fake, minCount: 4);
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(calls[0].Method).IsEqualTo("initialize");
        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[2].Method).IsEqualTo("session/set_config_option");
        await Assert.That(calls[3].Method).IsEqualTo("session/prompt");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    // ── Review-flow reviewer foundation: result-channel MCP + fail-closed pre-spawn validation ───

    /// <summary>A review-flow launch context: unattended-capable synthetic vendor, owned worktree
    /// (the default), a resolvable server url + kcap path, plus an optional MCP allowlist.</summary>
    static RuntimeStartContext ReviewContext(string[]? allowlist = null) =>
        MakeContext("agent-1") with {
            IsReviewFlow = true,
            ServerUrl    = "http://kcap.test",
            McpAllowlist = allowlist
        };

    /// <summary>A factory whose connectionSource INCREMENTS a counter (never throws — a throw would
    /// be swallowed by StartAsync's own handshake catch and mask the assertion) so a test can prove
    /// the child process was never spawned when pre-spawn validation refuses a launch.</summary>
    static (AcpHostedAgentRuntimeFactory Factory, Func<int> SpawnCount) CountingSpawnFactory(AcpVendorDescriptor descriptor) {
        var spawns = 0;
        var fake   = new FakeAcpAgent();
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: descriptor,
            config: new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: new CaptureServerConnection(),
            connectionSource: _ => { Interlocked.Increment(ref spawns); return (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()); });

        return (factory, () => Volatile.Read(ref spawns));
    }

    /// <summary>Test plan 2: session/new carries kcap-flow-result (both env vars) + one server per
    /// resolvable non-flow allowlist name (KCAP_URL only), with pinned command/args, exact JSON.</summary>
    [Test]
    public async Task ReviewFlow_SessionNew_CarriesFlowResultAndAllowlistServers_ExactJson() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: true);
        var fake        = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await RunSyntheticStartAsync(descriptor, fake, ReviewContext(["kcap-review"]), cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(mcpServersJson).IsEqualTo(
            """[{"name":"kcap-flow-result","command":"/usr/local/bin/kcap","args":["mcp","flow-result"],"env":[{"name":"KCAP_URL","value":"http://kcap.test"},{"name":"KCAP_FLOW_AGENT_ID","value":"agent-1"}]},{"name":"kcap-review","command":"/usr/local/bin/kcap","args":["mcp","review"],"env":[{"name":"KCAP_URL","value":"http://kcap.test"}]}]""");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>Test plan 3a: a repeated/case-varied auto-approvable id collapses to a single server
    /// (JsonObject-keying parity).</summary>
    [Test]
    public async Task ReviewFlow_DedupsAllowlistByCanonicalId() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: true);
        var fake        = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await RunSyntheticStartAsync(descriptor, fake, ReviewContext(["kcap-sessions", "KCAP-SESSIONS"]), cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(mcpServersJson).IsEqualTo(
            """[{"name":"kcap-flow-result","command":"/usr/local/bin/kcap","args":["mcp","flow-result"],"env":[{"name":"KCAP_URL","value":"http://kcap.test"},{"name":"KCAP_FLOW_AGENT_ID","value":"agent-1"}]},{"name":"kcap-sessions","command":"/usr/local/bin/kcap","args":["mcp","sessions"],"env":[{"name":"KCAP_URL","value":"http://kcap.test"}]}]""");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>The reserved `kcap-flow-result` id (always injected separately; the server's
    /// dynamic-flow policy legitimately lists it) is a no-op in the allowlist — not a rejection —
    /// and is not double-injected.</summary>
    [Test]
    public async Task ReviewFlow_ReservedFlowResultIdInAllowlist_IsNoOp_NotRejected() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: true);
        var fake        = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await RunSyntheticStartAsync(descriptor, fake, ReviewContext(["kcap-flow-result", "KCAP-FLOW-RESULT", "kcap-review"]), cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        // Exactly one flow-result server + kcap-review; the redundant allowlist entries are dropped.
        await Assert.That(mcpServersJson).IsEqualTo(
            """[{"name":"kcap-flow-result","command":"/usr/local/bin/kcap","args":["mcp","flow-result"],"env":[{"name":"KCAP_URL","value":"http://kcap.test"},{"name":"KCAP_FLOW_AGENT_ID","value":"agent-1"}]},{"name":"kcap-review","command":"/usr/local/bin/kcap","args":["mcp","review"],"env":[{"name":"KCAP_URL","value":"http://kcap.test"}]}]""");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>Test plan 3b: an allowlist entry that is flow-starting (recursion guard), unknown, or a
    /// non-auto-approvable write server (kcap-memory) fails the launch BEFORE spawn — the reviewer
    /// runs under the auto-approve bridge, so a write server must never reach it. Matches the
    /// authoritative read-only reviewer policy the orchestrator enforces for Codex.</summary>
    [Test]
    [Arguments("kcap-flows")]
    [Arguments("KCAP-FLOWS")]
    [Arguments("kcap-memory")]
    [Arguments("kcap-workitems")]
    [Arguments("kcap-analytics")]
    [Arguments("totally-unknown")]
    public async Task ReviewFlow_NonAutoApprovableAllowlistEntry_ThrowsBeforeSpawn(string entry) {
        var (factory, spawns) = CountingSpawnFactory(SyntheticDescriptor(supportsMcpServers: true));

        // kcap-sessions is auto-approvable; the offending entry must still fail the whole launch.
        await Assert.That(async () => await factory.StartAsync(ReviewContext(["kcap-sessions", entry]), CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(spawns()).IsEqualTo(0);
    }

    /// <summary>Test plan 4: a review flow missing the server url or kcap path can't build a result
    /// channel — StartAsync throws BEFORE the connectionSource is ever invoked (no leaked child).</summary>
    [Test]
    public async Task ReviewFlow_MissingServerUrl_ThrowsBeforeSpawn() {
        var (factory, spawns) = CountingSpawnFactory(SyntheticDescriptor(supportsMcpServers: true));

        await Assert.That(async () => await factory.StartAsync(ReviewContext() with { ServerUrl = null }, CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(spawns()).IsEqualTo(0);
    }

    [Test]
    public async Task ReviewFlow_WhitespaceCapacitorPath_ThrowsBeforeSpawn() {
        var (factory, spawns) = CountingSpawnFactory(SyntheticDescriptor(supportsMcpServers: true));

        await Assert.That(async () => await factory.StartAsync(ReviewContext() with { CapacitorPath = "   " }, CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(spawns()).IsEqualTo(0);
    }

    /// <summary>Test plan 5: an unattended-capable vendor with no ACP mcpServers support can't carry
    /// the result channel — throws before spawn.</summary>
    [Test]
    public async Task ReviewFlow_NoMcpServerSupport_ThrowsBeforeSpawn() {
        var (factory, spawns) = CountingSpawnFactory(SyntheticDescriptor(supportsMcpServers: false));

        await Assert.That(async () => await factory.StartAsync(ReviewContext(), CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(spawns()).IsEqualTo(0);
    }

    /// <summary>Test plan 6: a borrowed cwd, and separately a non-unattended vendor, both fail closed
    /// before spawn. Plus BuildProcessStartInfo's defense-in-depth borrowed-cwd refusal.</summary>
    [Test]
    public async Task ReviewFlow_BorrowedCwd_ThrowsBeforeSpawn() {
        var (factory, spawns) = CountingSpawnFactory(SyntheticDescriptor(supportsMcpServers: true));

        await Assert.That(async () => await factory.StartAsync(ReviewContext() with { Work = WorkLocation.BorrowedCwd }, CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(spawns()).IsEqualTo(0);
    }

    [Test]
    public async Task ReviewFlow_NotUnattended_ThrowsBeforeSpawn() {
        var (factory, spawns) = CountingSpawnFactory(NonUnattendedDescriptor());

        await Assert.That(async () => await factory.StartAsync(ReviewContext(), CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(spawns()).IsEqualTo(0);
    }

    [Test]
    public async Task BuildProcessStartInfo_Throws_ForReviewFlow_WhenBorrowedCwd_NoTrustArgvBuilt() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: true);
        var config     = new DaemonConfig();

        await Assert.That(() => AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            descriptor, config, MakeContext("agent-1") with { IsReviewFlow = true, Work = WorkLocation.BorrowedCwd }
        )).Throws<InvalidOperationException>();
    }

    /// <summary>Test plan 7: a blank/whitespace agent id would still yield a non-empty MCP list and
    /// slip past a count-only guard — it must fail closed before spawn.</summary>
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task ReviewFlow_BlankAgentId_ThrowsBeforeSpawn(string agentId) {
        var (factory, spawns) = CountingSpawnFactory(SyntheticDescriptor(supportsMcpServers: true));

        await Assert.That(async () => await factory.StartAsync(ReviewContext() with { AgentId = agentId }, CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(spawns()).IsEqualTo(0);
    }

    /// <summary>Test plan 11: for an owned-worktree unattended review flow, the factory computes
    /// autoApprove=true and threads it to the bridge — an inbound permission request is auto-approved
    /// (least-privilege allow) WITHOUT ever routing to the injected server connection (no human).</summary>
    [Test]
    public Task ReviewFlow_SyntheticOwnedWorktree_Unattended_AutoApprovesPermission_WithoutRoutingToHuman() =>
        AssertReviewFlowAutoApprovesPermissionAsync(
            SyntheticDescriptor(supportsMcpServers: true),
            ReviewContext());

    /// <summary>A borrowed Copilot reviewer passed the descriptor's capability-clamp gate and is
    /// just as unattended as an owned-worktree reviewer. Its ACP permission request must therefore
    /// resolve locally rather than waiting forever on a human interaction decision.</summary>
    [Test]
    public Task ReviewFlow_CopilotBorrowedCwd_Unattended_AutoApprovesPermission_WithoutRoutingToHuman() =>
        AssertReviewFlowAutoApprovesPermissionAsync(
            AcpVendorDescriptors.Copilot,
            ReviewContext(["kcap-review"]) with { Work = WorkLocation.BorrowedCwd });

    /// <summary>Cursor's launch flags are responsible for producing zero interaction frames. If a
    /// future Cursor build regresses and emits one anyway, kcap must not auto-approve it or route it
    /// to a human: the reviewer is immediately reaped.</summary>
    [Test]
    public async Task ReviewFlow_Cursor_PermissionFrame_ReapsReviewer_WithoutRoutingOrApproval() {
        var fake       = new FakeAcpAgent();
        var connection = new CaptureServerConnection();
        var process    = new FakeAcpProcess();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, process));

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);
        var started = await factory.StartAsync(ReviewContext(), cts.Token).WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Read file"}""",
            optionsJson: """[{"optionId":"ao","name":"Allow once","kind":"allow_once"}]""");
        await started.Runtime.SendUserInputAsync("review").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (!process.HasExited && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(process.HasExited).IsTrue();
        await Assert.That(connection.RequestAcpInteractionAsyncCalled).IsFalse();

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    static async Task AssertReviewFlowAutoApprovesPermissionAsync(
            AcpVendorDescriptor descriptor,
            RuntimeStartContext context) {
        var fake        = new FakeAcpAgent();
        var connection  = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: descriptor,
            config: new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()));

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await factory.StartAsync(context, cts.Token).WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Read file"}""",
            optionsJson: """[{"optionId":"ao","name":"Allow once","kind":"allow_once"},{"optionId":"d","name":"Deny","kind":"reject_once"}]""");

        await started.Runtime.SendUserInputAsync("review").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.LastServerRequestResponse is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var outcome = fake.LastServerRequestResponse!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("selected");
        await Assert.That(outcome.GetProperty("optionId").GetString()).IsEqualTo("ao");
        // The bridge auto-approved locally: the server connection was never consulted.
        await Assert.That(connection.RequestAcpInteractionAsyncCalled).IsFalse();

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    // ── Copilot descriptor (hosted-agent registration) ───────────────────────────────────────────

    /// <summary>Copilot spawns `copilot --acp --stdio` from `DaemonConfig.CopilotPath`.</summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_SpawnsAcpStdioArgv() {
        var config = new DaemonConfig { CopilotPath = "/opt/homebrew/bin/copilot" };
        var ctx    = MakeContext("agent-1");

        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(AcpVendorDescriptors.Copilot, config, ctx);

        await Assert.That(psi.FileName).IsEqualTo("/opt/homebrew/bin/copilot");
        await Assert.That(psi.ArgumentList.SequenceEqual(["--acp", "--stdio"])).IsTrue();
        await Assert.That(psi.WorkingDirectory).IsEqualTo(ctx.Worktree.Path);
    }

    /// <summary>An unattended Copilot reviewer starts trusted, disables ambient/custom tools,
    /// preloads only its validated stdio MCP servers, and exposes only the two flow-channel tools plus
    /// the reviewed-safe tools from the requested server. Copilot's allowlist consumes flattened
    /// runtime ids (<c>server-tool</c>), not permission-pattern syntax.</summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_ReviewFlow_PreloadsMcpAndClampsTools() {
        var config = new DaemonConfig { CopilotPath = "/opt/homebrew/bin/copilot" };
        var ctx    = ReviewContext(["kcap-review"]);

        var psi  = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(AcpVendorDescriptors.Copilot, config, ctx);
        var argv = psi.ArgumentList.ToArray();

        await Assert.That(argv.SequenceEqual([
            "--acp",
            "--stdio",
            "--allow-all-tools",
            "--no-ask-user",
            "--no-custom-instructions",
            "--disable-builtin-mcps",
            "--additional-mcp-config",
            """{"mcpServers":{"kcap-flow-result":{"type":"stdio","command":"/usr/local/bin/kcap","args":["mcp","flow-result"],"env":{"KCAP_URL":"http://kcap.test","KCAP_FLOW_AGENT_ID":"agent-1"}},"kcap-review":{"type":"stdio","command":"/usr/local/bin/kcap","args":["mcp","review"],"env":{"KCAP_URL":"http://kcap.test"}}}}""",
            "--available-tools=kcap-flow-result-submit_review_result",
            "--available-tools=kcap-flow-result-send_flow_message",
            "--available-tools=kcap-review-get_file_context",
            "--available-tools=kcap-review-get_pr_summary",
            "--available-tools=kcap-review-get_transcript",
            "--available-tools=kcap-review-list_pr_files",
            "--available-tools=kcap-review-list_sessions",
            "--available-tools=kcap-review-search_context"
        ])).IsTrue();
    }

    /// <summary>The Copilot config builder must use the NativeAOT-safe JsonNode.Parse string path,
    /// while still escaping every server-controlled string correctly. Values chosen here cover
    /// quotes, backslashes, and newlines in the command and environment values.</summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_McpConfig_AotSafeStringsRemainValidJson() {
        var ctx = ReviewContext() with {
            AgentId      = "agent-\"quoted\"\\line\nnext",
            ServerUrl    = "https://kcap.test/\"quoted\"\\line\nnext",
            CapacitorPath = "/path/with \"quote\"/kcap"
        };

        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Copilot, new DaemonConfig(), ctx);
        var argv = psi.ArgumentList.ToArray();
        var configIndex = Array.IndexOf(argv, "--additional-mcp-config");
        var root = System.Text.Json.Nodes.JsonNode.Parse(argv[configIndex + 1])!.AsObject();
        var server = root["mcpServers"]!["kcap-flow-result"]!.AsObject();

        await Assert.That(server["command"]!.GetValue<string>()).IsEqualTo("/path/with \"quote\"/kcap");
        await Assert.That(server["env"]!["KCAP_URL"]!.GetValue<string>())
            .IsEqualTo("https://kcap.test/\"quoted\"\\line\nnext");
        await Assert.That(server["env"]!["KCAP_FLOW_AGENT_ID"]!.GetValue<string>())
            .IsEqualTo("agent-\"quoted\"\\line\nnext");
    }

    /// <summary>Copilot's process-level available-tools clamp removes every ambient shell/file
    /// tool, so its unattended reviewer can safely use the server's default same-machine borrowed
    /// checkout. Other ACP descriptors remain owned-worktree-only.</summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_BorrowedReviewFlow_IsAllowedAndStillClamped() {
        var ctx = ReviewContext(["kcap-review"]) with { Work = WorkLocation.BorrowedCwd };

        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Copilot, new DaemonConfig(), ctx);

        await Assert.That(psi.ArgumentList).Contains("--allow-all-tools");
        await Assert.That(psi.ArgumentList).Contains("--available-tools=kcap-flow-result-submit_review_result");
        await Assert.That(psi.ArgumentList).Contains("--available-tools=kcap-flow-result-send_flow_message");
        await Assert.That(psi.ArgumentList.Any(a => a.StartsWith("--available-tools=kcap-review-", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>Cursor cannot safely run directly in the borrowed checkout. The orchestrator must
    /// first materialize its authorized contents into an independent snapshot.</summary>
    [Test]
    public async Task BuildProcessStartInfo_Cursor_RawBorrowedReviewRequiresSnapshotMaterialization() {
        var ctx = ReviewContext() with { Work = WorkLocation.BorrowedCwd };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Cursor, new DaemonConfig(), ctx));

        await Assert.That(ex.Message).Contains("snapshot materialization");
    }

    /// <summary>A full StartAsync for the Copilot descriptor: handshake completes, the started
    /// runtime's Vendor is `copilot`, and an interactive launch sends `mcpServers: []`.</summary>
    [Test]
    public async Task StartAsync_Copilot_Handshake_VendorCopilot_AndEmptyMcpServers() {
        var fake = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started        = await RunSyntheticStartAsync(AcpVendorDescriptors.Copilot, fake, MakeContext("agent-1"), cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(started.Runtime.Vendor).IsEqualTo("copilot");
        await Assert.That(mcpServersJson).IsEqualTo("[]");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>Copilot's review MCP arrives through process arguments, so ACP session/new remains
    /// empty while pre-spawn validation still accepts the alternate transport.</summary>
    [Test]
    public async Task StartAsync_Copilot_ReviewFlow_UsesProcessTransport_AndSessionNewMcpEmpty() {
        var fake = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started        = await RunSyntheticStartAsync(AcpVendorDescriptors.Copilot, fake, ReviewContext(["kcap-review"]), cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(mcpServersJson).IsEqualTo("[]");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>Copilot uses the shared config-option selector, so a requested model is resolved
    /// against session/new's advertised models and applied before the initial prompt.</summary>
    [Test]
    public async Task StartAsync_Copilot_ModelOverride_SendsSetConfigOption() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId,
            currentModelId: "auto",
            availableModels: [("auto", "Auto"), ("gpt-5-mini", "GPT-5 mini")]));

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await RunSyntheticStartAsync(
            AcpVendorDescriptors.Copilot,
            fake,
            ReviewContext() with { Model = "gpt-5-mini" },
            cts.Token);

        var calls = await WaitForCallCountAsync(fake, minCount: 3);
        var setConfigCall = calls.Single(c => c.Method == "session/set_config_option");
        await Assert.That(setConfigCall.Params!.Value.GetProperty("configId").GetString()).IsEqualTo("model");
        await Assert.That(setConfigCall.Params!.Value.GetProperty("value").GetString()).IsEqualTo("gpt-5-mini");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>Test plan item 6 (false branch): Copilot ships `SupportsMcpServers: false` (live
    /// probe — Copilot advertises http/sse MCP, not stdio), so even a populated `ctx.McpServers` is
    /// gated out and `session/new.mcpServers` stays `[]` — the descriptor never forwards a stdio
    /// server the vendor can't consume.</summary>
    [Test]
    public async Task StartAsync_Copilot_SupportsMcpServersFalse_PopulatedContext_GatedOut() {
        var fake = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        AcpMcpServerSpec[] mcpServers = [
            new AcpMcpServerSpec(Name: "kcap-flow-result", Command: "kcap", Args: ["mcp", "flow-result"], Env: [])
        ];
        var ctx = MakeContext("agent-1") with { McpServers = mcpServers };

        var started        = await RunSyntheticStartAsync(AcpVendorDescriptors.Copilot, fake, ctx, cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(mcpServersJson).IsEqualTo("[]");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }
}
