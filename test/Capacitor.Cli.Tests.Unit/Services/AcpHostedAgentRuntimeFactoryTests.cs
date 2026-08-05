using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// The PRODUCTION factory must report its descriptor's model-selection capability. This is the seam
    /// the orchestrator reads, and nothing else asserted it.
    ///
    /// <para>Found by code review, and confirmed by mutation: deleting
    /// <c>AcpHostedAgentRuntimeFactory.SupportsModelSelection</c> makes it fall back to the interface's
    /// default <c>true</c> — silently reintroducing the reported-vs-running model mismatch — and **all
    /// 206 other tests still passed**. The descriptor tests proved
    /// <c>Kiro.ModelSelector.CanSelectModel == false</c>, and the orchestrator tests proved the behaviour
    /// when a SPY factory reports false, but nothing connected the two through the real factory.</para>
    ///
    /// <para>Cursor and Copilot are asserted alongside Kiro deliberately: a mutation that hard-coded
    /// <c>false</c> instead of delegating would satisfy a Kiro-only assertion while breaking model
    /// selection for the two vendors that do support it.</para>
    ///
    /// <para><b>Read through <see cref="IHostedAgentRuntimeFactory"/>, not the concrete type</b>, because
    /// that is how <c>AgentOrchestrator</c> consumes it — and because it makes the guard behavioural
    /// rather than incidental. Typed concretely, deleting the override produces a COMPILE error (a
    /// default interface member is not accessible through the implementing type), which happens to break
    /// the build but never exercises the value the orchestrator would actually observe. Through the
    /// interface, the deletion instead surfaces as the interface default <c>true</c> and fails this test
    /// on the exact value that would have caused the reported-vs-running mismatch.</para>
    /// </summary>
    [Test]
    public async Task SupportsModelSelection_DelegatesToTheDescriptorsSelector_ForEachRealVendor() {
        static IHostedAgentRuntimeFactory Build(AcpVendorDescriptor descriptor) =>
            new AcpHostedAgentRuntimeFactory(descriptor: descriptor,
                config: new DaemonConfig(),
                loggerFactory: NullLoggerFactory.Instance,
                connection: new CaptureServerConnection(),
                // Never spawns: this test only reads a capability property, never calls StartAsync.
                connectionSource: _ => throw new InvalidOperationException(
                    "SupportsModelSelection must not spawn a process."));

        await Assert.That(Build(AcpVendorDescriptors.Kiro).SupportsModelSelection).IsFalse();
        await Assert.That(Build(AcpVendorDescriptors.Cursor).SupportsModelSelection).IsTrue();
        await Assert.That(Build(AcpVendorDescriptors.Copilot).SupportsModelSelection).IsTrue();
    }

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

    /// <summary>An unattended reviewer's MCP surface is auditable after the fact, and the logged
    /// names are the RESOLVED names — not the caller's raw allowlist.
    ///
    /// <para>The first version of this test was vacuous in the way that matters, and review caught it:
    /// it fed the already-canonical <c>["kcap-review"]</c> and asserted with Contains/DoesNotContain,
    /// so a build logging <c>ctx.McpAllowlist</c> verbatim — before canonicalisation, dedup, and
    /// stripping of flow-STARTING servers — passed every assertion. Worse, the
    /// <c>DoesNotContain("kcap-flows")</c> was meaningless because that value was never supplied.</para>
    ///
    /// <para>So the input is deliberately hostile: mixed case, surrounding whitespace, a duplicate, a
    /// redundant explicit result channel, and <c>kcap-flows</c> itself — the one server that must never
    /// survive. The assertion is then EXACT and ordered, because only an exact comparison can tell the
    /// resolved surface from the requested one.</para></summary>
    [Test]
    public async Task StartAsync_ReviewFlow_LogsTheRESOLVEDReviewerMcpSurface_NotTheRawAllowlist() {
        var fake          = new FakeAcpAgent();
        var connection    = new CaptureServerConnection();
        var loggerFactory = new CaptureLoggerFactory();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: SyntheticDescriptor(supportsMcpServers: true),
            config: new DaemonConfig(),
            loggerFactory: loggerFactory,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()));

        using var cts   = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        // Hostile but VALID input: casing, whitespace, a duplicate, and a redundant explicit result
        // channel. Deliberately no `kcap-flows` — on this path a flow-starting server does not get
        // silently stripped, it makes the whole launch throw "not auto-approvable" before any spawn
        // (pinned by ReviewFlow_NonAutoApprovableAllowlistEntry_ThrowsBeforeSpawn). That is a stronger
        // isolation guarantee than filtering, so the surface log can never be the thing that catches
        // it; what this test must catch is the log echoing the RAW allowlist instead of the resolved
        // one, which casing/whitespace/duplicates expose on their own.
        var ctx = ReviewContext([
            "  KCAP-Review  ", "kcap-review",
            KcapMcpRegistry.ReservedResultChannelId
        ]) with { AgentId = "agent-mcp-surface" };

        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var line = loggerFactory.Logger.Entries
            .Where(e => e.Level == LogLevel.Information)
            .Select(e => e.Message)
            .FirstOrDefault(m => m.Contains("ACP reviewer MCP surface"));

        await Assert.That(line).IsNotNull()
            .Because("the reviewer's MCP surface must be observable in the record, not only in a test");

        // EXACT, ordered — the load-bearing assertion. Anything logging the raw allowlist fails here.
        var logged = System.Text.RegularExpressions.Regex.Match(line!, @"servers=\[([^\]]*)\]").Groups[1].Value;
        var sent   = await WaitForSessionNewServerNamesAsync(fake);

        await Assert.That(logged).IsEqualTo(string.Join(",", sent))
            .Because("the logged surface must equal what session/new actually carried");
        // Resolved, not echoed: the raw input had three entries with mixed casing, padding and a
        // duplicate; the surface must be canonical and deduplicated. A build logging ctx.McpAllowlist
        // verbatim fails here on every count.
        await Assert.That(logged.Split(',').Count(n => n == "kcap-review")).IsEqualTo(1);
        await Assert.That(logged).DoesNotContain("KCAP-Review");
        await Assert.That(logged).DoesNotContain(" ");
        await Assert.That(logged.Split(',').Length).IsEqualTo(2);
        await Assert.That(logged).Contains(KcapMcpRegistry.ReservedResultChannelId);
        await Assert.That(line!).Contains(AcpReviewFlowMcpTransport.SessionNew.ToString());
        await Assert.That(line!).Contains("agent-mcp-surface");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>The audit line must describe what CROSSED THE WIRE, not what was resolved. A failed
    /// handshake must leave no surface line at all — otherwise the record claims a reviewer held tools
    /// when no reviewer session ever existed, which is worse than having no record.</summary>
    [Test]
    public async Task StartAsync_ReviewFlow_FailedHandshake_LogsNoReviewerMcpSurface() {
        var fake          = new FakeAcpAgent();
        var connection    = new CaptureServerConnection();
        var loggerFactory = new CaptureLoggerFactory();
        fake.FailNextInitialize(-32000, "initialize rejected");

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: SyntheticDescriptor(supportsMcpServers: true),
            config: new DaemonConfig(),
            loggerFactory: loggerFactory,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()));

        using var cts   = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        await Assert.That(async () => await factory.StartAsync(
            ReviewContext(["kcap-review"]), cts.Token).WaitAsync(HangGuard)).ThrowsException();

        await Assert.That(loggerFactory.Logger.Entries.Any(e => e.Message.Contains("ACP reviewer MCP surface")))
            .IsFalse()
            .Because("a reviewer that never completed its handshake received no surface to record");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await fake.DisposeAsync();
    }

    /// <summary>The other half of "what crossed the wire": a session that WAS established and WAS handed
    /// the surface must be recorded even when the launch then fails. StartAsync keeps working after
    /// session/new — it awaits model selection, which propagates cancellation — so keying the emit on
    /// StartAsync's normal return silently loses the record for an established session whose daemon
    /// token was cancelled a moment later. Cancelling INSIDE model selection is the reachable
    /// interleaving; without this test the completeness half of the audit claim is unpinned.</summary>
    [Test]
    public async Task StartAsync_ReviewFlow_CancelledDuringModelSelection_StillLogsTheEstablishedSurface() {
        var fake          = new FakeAcpAgent();
        var connection    = new CaptureServerConnection();
        var loggerFactory = new CaptureLoggerFactory();

        // Blocks in model selection — i.e. AFTER session/new has completed and SessionId is assigned —
        // until the launch token is cancelled, then propagates like the real selector does.
        var enteredSelection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selector         = new BlockingModelSelector(enteredSelection);

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: SyntheticDescriptor(supportsMcpServers: true, modelSelector: selector),
            config: new DaemonConfig(),
            loggerFactory: loggerFactory,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()));

        using var cts   = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var startTask = factory.StartAsync(ReviewContext(["kcap-review"]), cts.Token);

        // Proves the wire state before cancelling: selection is only reached once session/new returned.
        await enteredSelection.Task.WaitAsync(HangGuard);

        var sent = await WaitForSessionNewServerNamesAsync(fake);
        await Assert.That(sent).IsNotEmpty()
            .Because("the surface must already have crossed the wire when selection is entered");

        cts.Cancel();

        await Assert.That(async () => await startTask.WaitAsync(HangGuard)).ThrowsException()
            .Because("a cancelled launch must still fail; the record is what survives, not the launch");

        var line = loggerFactory.Logger.Entries
            .Select(e => e.Message)
            .FirstOrDefault(m => m.Contains("ACP reviewer MCP surface"));

        await Assert.That(line).IsNotNull()
            .Because("the session was established and handed this surface — dropping the record because "
                   + "cancellation arrived during model selection makes the audit log silently incomplete");

        var logged = System.Text.RegularExpressions.Regex.Match(line!, @"servers=\[([^\]]*)\]").Groups[1].Value;
        await Assert.That(logged).IsEqualTo(string.Join(",", sent))
            .Because("the recorded surface must still equal what session/new carried");

        // Exactly one line: the success path and the failure path must not both fire.
        await Assert.That(loggerFactory.Logger.Entries.Count(e => e.Message.Contains("ACP reviewer MCP surface")))
            .IsEqualTo(1);

        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await fake.DisposeAsync();
    }

    /// <summary>Parks in model selection until cancelled, so a test can occupy the window between a
    /// completed session/new and StartAsync's return.</summary>
    sealed class BlockingModelSelector(TaskCompletionSource entered) : IAcpModelSelector {
        // It does attempt selection (that is the point — it parks mid-attempt), so it reports true.
        public bool CanSelectModel => true;

        public async Task<string?> TrySelectAsync(
                AcpConnection            connection,
                string                   sessionId,
                System.Text.Json.JsonElement sessionNewResult,
                string?           requestedModel,
                ILogger           logger,
                CancellationToken ct) {
            entered.TrySetResult();

            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);

            return null;
        }
    }

    /// <summary>Reads the server names session/new actually carried, so the log can be compared to the
    /// wire rather than to a restatement of the same intent.</summary>
    static async Task<string[]> WaitForSessionNewServerNamesAsync(FakeAcpAgent fake) {
        var json = await WaitForSessionNewMcpServersJsonAsync(fake);

        return System.Text.Json.JsonDocument.Parse(json).RootElement
            .EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToArray();
    }

    /// <summary>The paired direction: a NON-review launch logs no reviewer surface. Logging it for
    /// every launch would bury the signal an auditor is looking for in ordinary interactive noise.</summary>
    [Test]
    public async Task StartAsync_NonReviewLaunch_LogsNoReviewerMcpSurface() {
        var fake          = new FakeAcpAgent();
        var connection    = new CaptureServerConnection();
        var loggerFactory = new CaptureLoggerFactory();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "cursor-agent" },
            loggerFactory: loggerFactory,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()));

        using var cts   = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);
        var started     = await factory.StartAsync(MakeContext("agent-plain"), cts.Token).WaitAsync(HangGuard);

        await Assert.That(loggerFactory.Logger.Entries.Any(e => e.Message.Contains("ACP reviewer MCP surface")))
            .IsFalse();

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
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
        // Elicitation capability flip: the LIVE StartAsync path must advertise form-mode (the bare
        // {} is the schema's "supported" signal) and must never advertise url-mode — asserted here
        // through the real runtime rather than only on the hand-built InitializeParams (see
        // InitializeCapabilityAdvertisementTests for the full-payload pin).
        await Assert.That(initializeCall.Params!.Value.GetProperty("clientCapabilities").GetProperty("elicitation").GetProperty("form").GetRawText()).IsEqualTo("{}");
        await Assert.That(initializeCall.Params!.Value.GetProperty("clientCapabilities").GetProperty("elicitation").TryGetProperty("url", out _)).IsFalse();

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
    /// <summary>The COMPLETE set of <c>--available-tools</c> arguments a Copilot review launch may
    /// emit: the reserved flow-result channel's two tools, every unattended-safe tool of each
    /// allowlisted MCP server, plus whatever extra ids the borrowed policy contributes.
    ///
    /// <para>Derived from <c>KcapMcpRegistry</c> rather than hand-listed, so a server gaining a tool
    /// updates the expectation with the production code — but derived only from the READ-ONLY
    /// registry the review path already trusts, so the test still fails if the argv builder starts
    /// emitting an id from anywhere else.</para></summary>
    static string[] ExpectedAvailableTools(string[] allowlisted, IReadOnlyList<string> extra) => [
        .. KcapMcpRegistry.ReservedResultChannelTools
              .Where(t => t.UnattendedSafe)
              .Select(t => $"--available-tools={KcapMcpRegistry.ReservedResultChannelId}-{t.Name}"),
        "--available-tools=kcap-review-context-get_branch_authored_mcp_configs",
        .. allowlisted.SelectMany(name => KcapMcpRegistry.ReviewFlowUnattendedSafeTools[name]
                                             .Order(StringComparer.Ordinal)
                                             .Select(t => $"--available-tools={name}-{t}")),
        .. extra.Select(t => $"--available-tools={t}")
    ];

    static AcpVendorDescriptor SyntheticDescriptor(
            bool supportsMcpServers,
            bool borrowedReview = false,
            AcpBorrowedReviewContainment containment = AcpBorrowedReviewContainment.None,
            IAcpModelSelector? modelSelector = null) => new(
        Vendor:              "test-acp-vendor",
        ResolveBinaryPath:   _ => "test-acp-vendor-cli",
        ResolveDefaultModel: _ => null,
        Argv:                ["acp", "--flag-a"],
        UnattendedTrustArgv: ["--trust"],
        SupportsUnattended:  true,
        ModelSelector:       modelSelector ?? NoOpModelSelector.Instance,
        SupportsMcpServers:  supportsMcpServers,
        SupportsBorrowedReviewFlow: borrowedReview,
        BorrowedReviewContainment:  containment,
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
            McpAllowlist = allowlist,
            ReviewContextCapabilityUrl =
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs"
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

    /// <summary>An unattended Copilot reviewer's ACP permission request must resolve locally rather
    /// than waiting forever on a human decision.
    ///
    /// <para>Now exercised on an OWNED worktree: Copilot no longer accepts a raw borrowed cwd (it
    /// requires snapshot materialization), so the old borrowed-cwd context is not a launchable
    /// configuration any more.</para></summary>
    [Test]
    public Task ReviewFlow_CopilotUnattended_AutoApprovesPermission_WithoutRoutingToHuman() =>
        AssertReviewFlowAutoApprovesPermissionAsync(
            AcpVendorDescriptors.Copilot,
            ReviewContext(["kcap-review"]) with { Work = WorkLocation.OwnedWorktree });

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

    /// <summary>Copilot, like Cursor, cannot run directly in the requester's borrowed checkout — the
    /// orchestrator must materialize a daemon-owned snapshot first.
    ///
    /// <para>This replaces a test that asserted the opposite ("is allowed and still clamped"), whose
    /// doc comment recorded the premise this issue disproves: that the available-tools clamp removing
    /// every ambient shell/file tool made a raw borrowed checkout safe. It did make it safe — and
    /// also unreadable, which is the defect.</para></summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_RawBorrowedReviewRequiresSnapshotMaterialization() {
        var ctx = ReviewContext(["kcap-review"]) with { Work = WorkLocation.BorrowedCwd };

        // Pinned to the supported entry: the claim is "even where borrowed review IS available,
        // the raw checkout is refused". Reading the host's own entry would make this test pass
        // vacuously wherever Copilot is unverified — it would take the not-supported arm instead.
        var supported = CopilotBorrowedReviewPolicy.Resolve(OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Copilot, new DaemonConfig(), ctx, supported));

        await Assert.That(ex.Message).Contains("snapshot materialization");
    }

    /// <summary>The readable borrowed surface: a borrowed-SNAPSHOT Copilot review keeps the exclusive
    /// allowlist and widens it with the verified read tools, so the reviewer can actually read the
    /// snapshot it was given.
    ///
    /// <para>The allowlist stays exclusive on purpose. Live probing found <c>--deny-tool=write</c>
    /// does not cover Copilot's file-create tool: a direct write to an outside absolute path is not
    /// denied but raises a path-trust permission request, which this daemon's unattended policy
    /// auto-approves. Exclusivity makes write/exec unrepresentable, so no such request exists.</para></summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_BorrowedSnapshot_AllowsReadToolsWithinTheExclusiveAllowlist() {
        var ctx = ReviewContext(["kcap-review"]) with {
            Work = WorkLocation.OwnedWorktree, IsBorrowedSnapshot = true
        };

        // Explicit supported entry, so this asserts the argv on any host platform — the host's own
        // entry may be unverified, which is a separate concern covered by the policy matrix.
        var supported = CopilotBorrowedReviewPolicy.Resolve(OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);
        var psi  = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Copilot, ResolvableConfig(), ctx, supported, BrokeredEnv());
        var argv = psi.ArgumentList.ToArray();

        // EXACT set equality, not contains-plus-a-denylist. A named-exclusion assertion only rules
        // out the ids someone thought to name: a build that also emitted --available-tools=web_fetch,
        // =task, =apply_patch, a future mutating alias, or a wildcard would satisfy every "contains"
        // and every "does not contain create/edit/bash" check while handing the reviewer exactly the
        // surface this issue exists to withhold. Exclusivity IS the security boundary, so the test
        // has to pin the whole set.
        var emitted = argv.Where(a => a.StartsWith("--available-tools=")).ToArray();

        await Assert.That(emitted).IsEquivalentTo(ExpectedAvailableTools(
            allowlisted: ["kcap-review"], extra: CopilotBorrowedReviewPolicy.ReadToolIds));

        string[] expected = ExpectedAvailableTools(
            allowlisted: ["kcap-review"], extra: CopilotBorrowedReviewPolicy.ReadToolIds);
        // Duplicates would not change the effective set but would signal the argv builder losing
        // track of what it emitted, so pin the count too.
        await Assert.That(emitted.Length).IsEqualTo(expected.Length);
        // No broad escape hatch alongside the allowlist.
        await Assert.That(argv.Any(a => a.Contains("--allow-all-paths") || a.Contains("--yolo")
                                     || a.Contains("--add-dir") || a.Contains("--deny-tool"))).IsFalse();
    }

    /// <summary>The seam the other argv tests deliberately bypass: production resolution, with NO
    /// policy passed, on whatever host this runs on.
    ///
    /// <para>Every other borrowed-argv test pins an explicit supported entry so it can assert the
    /// argv anywhere. That is right for those tests and wrong as the only coverage: it means a
    /// regression that made <c>BuildProcessStartInfo</c> resolve from the static descriptor instead
    /// of the policy would keep them all green — advertisement would still read supported while a
    /// real spawn on a supported host received no read tools and reviewed blind, which is precisely
    /// the advertise/spawn split this design exists to prevent.</para>
    ///
    /// <para>So this one asserts the IMPLICATION rather than a fixed argv: whatever the host's own
    /// entry says it supports is what the host's own spawn must produce. It is meaningful on a
    /// supported host and on an unsupported one, and it fails on either if the two diverge.</para></summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_ProductionResolution_ArgvAgreesWithWhatThisHostAdvertises() {
        var host = AcpHostedAgentRuntimeFactory.PolicyFor(AcpVendorDescriptors.Copilot);
        var ctx  = ReviewContext(["kcap-review"]) with {
            Work = WorkLocation.OwnedWorktree, IsBorrowedSnapshot = true
        };

        if (!host.Supported) {
            // Unsupported host: the snapshot launch must not be buildable at all, let alone readable.
            var ex = Assert.Throws<InvalidOperationException>(() =>
                AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                    AcpVendorDescriptors.Copilot, new DaemonConfig(), ctx));

            await Assert.That(ex.Message).IsEqualTo("borrowed_snapshot_containment_mismatch");

            return;
        }

        // Supported host: no policy argument, so this is the production path end to end. The vendor
        // path must still resolve, or the fail-closed guard fires for a reason unrelated to the argv.
        var emitted = AcpHostedAgentRuntimeFactory
            .BuildProcessStartInfo(AcpVendorDescriptors.Copilot, ResolvableConfig(), ctx)
            .ArgumentList.Where(a => a.StartsWith("--available-tools=")).ToArray();

        await Assert.That(emitted).IsEquivalentTo(ExpectedAvailableTools(
            allowlisted: ["kcap-review"], extra: host.ExtraBorrowedToolIds));
    }

    /// <summary>A borrowed snapshot never auto-approves an interaction frame, whatever the vendor
    /// descriptor declares.
    ///
    /// <para>Without this the readable allowlist is a read-containment hole, not a fix: widening the
    /// allowlist also widens what a path-taking read tool can be pointed at, Copilot answers an
    /// outside-the-snapshot absolute path with a permission request, and AutoApprove grants it
    /// without inspecting the tool. Probed live — a reviewer read a file outside the snapshot and
    /// echoed it back through the result channel.</para></summary>
    [Test]
    [Arguments(true)]   // borrowed snapshot  -> Fail, overriding the descriptor
    [Arguments(false)]  // owned worktree     -> the descriptor's own AutoApprove
    public async Task ReviewFlow_Copilot_BorrowedSnapshot_TakesTheFailInteractionPolicy(bool borrowedSnapshot) {
        // Pinned against the descriptor's own declaration, so this fails if the override is dropped
        // AND if the non-borrowed path silently changes.
        await Assert.That(AcpVendorDescriptors.Copilot.UnattendedInteractionPolicy)
            .IsEqualTo(AcpUnattendedInteractionPolicy.AutoApprove);

        var expected = borrowedSnapshot
            ? AcpUnattendedInteractionPolicy.Fail
            : AcpUnattendedInteractionPolicy.AutoApprove;

        await Assert.That(AcpHostedAgentRuntimeFactory.ResolveUnattendedInteractionPolicy(
                ReviewContext(["kcap-review"]) with {
                    Work = WorkLocation.OwnedWorktree, IsBorrowedSnapshot = borrowedSnapshot
                },
                AcpVendorDescriptors.Copilot))
            .IsEqualTo(expected);
    }

    /// <summary>The production WIRING, not the resolver seam: a borrowed-snapshot launch reaches the
    /// bridge with the Fail policy and its reviewer is reaped, even though its descriptor declares
    /// <c>AutoApprove</c>.
    ///
    /// <para>The resolver test above proves <c>ResolveUnattendedInteractionPolicy</c> returns
    /// <c>Fail</c>; it does NOT prove <c>StartAsync</c> carries that value into the runtime and the
    /// bridge. A regression that left the helper intact and reverted the call site to the
    /// descriptor's policy would keep it green and reopen the outside-read hole. The one existing
    /// end-to-end reap test uses Cursor, whose descriptor already declares <c>Fail</c> — so it
    /// cannot distinguish "the override works" from "the descriptor happened to agree".</para>
    ///
    /// <para>Uses a synthetic descriptor declaring <c>AutoApprove</c>, so the override is the ONLY
    /// thing that can produce a reap here, and so this runs on every platform — Copilot's own entry
    /// is unsupported off macOS/arm64 and <c>StartAsync</c> would reject the launch before the
    /// bridge ever sees a frame.</para>
    ///
    /// <para>Only the permission frame is injected. That is sufficient rather than partial: the
    /// bridge's <c>Fail</c> check precedes method dispatch and parameter parsing, so it is one
    /// branch for both interaction methods, and <c>AcpInteractionBridgeTests</c>
    /// <c>FailPolicy_AnyInteraction_SignalsReap_WithoutRoutingToHuman</c> already covers the method
    /// axis. What is unproven WITHOUT this test is the wiring, and one frame proves the wiring.</para></summary>
    [Test]
    public async Task ReviewFlow_BorrowedSnapshot_PermissionFrame_IsReaped_EvenWhenTheDescriptorAutoApproves() {
        var descriptor = SyntheticDescriptor(
            supportsMcpServers: true,
            borrowedReview:     true,
            containment:        AcpBorrowedReviewContainment.IndependentSnapshot);

        // Load-bearing: the descriptor's own policy is AutoApprove, so any reap below is the override.
        await Assert.That(descriptor.UnattendedInteractionPolicy)
            .IsEqualTo(AcpUnattendedInteractionPolicy.AutoApprove);

        var fake       = new FakeAcpAgent();
        var connection = new CaptureServerConnection();
        var process    = new FakeAcpProcess();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: descriptor,
            config: new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, process));

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);
        var started = await factory.StartAsync(
            ReviewContext(["kcap-review"]) with {
                Work = WorkLocation.OwnedWorktree, IsBorrowedSnapshot = true
            }, cts.Token).WaitAsync(HangGuard);

        // The exact frame the live probe produced when the reviewer reached outside the snapshot.
        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Access paths outside trusted directories","kind":"read"}""",
            optionsJson: """[{"optionId":"allow_once","name":"Allow once","kind":"allow_once"}]""");
        await started.Runtime.SendUserInputAsync("review").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (!process.HasExited && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(process.HasExited).IsTrue();
        // Not granted, and not routed to a human either — a borrowed reviewer has none attached.
        await Assert.That(connection.RequestAcpInteractionAsyncCalled).IsFalse();

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>The paired direction: a NON-borrowed review under the same synthetic descriptor
    /// still auto-approves. Without this, a build that reaped every review-flow interaction frame —
    /// breaking Copilot's ordinary owned-worktree reviews — would pass the test above.</summary>
    [Test]
    public async Task ReviewFlow_NonBorrowed_PermissionFrame_IsStillAutoApproved() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: true);
        var fake       = new FakeAcpAgent();
        var connection = new CaptureServerConnection();
        var process    = new FakeAcpProcess();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: descriptor,
            config: new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, process));

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);
        var started = await factory.StartAsync(
            ReviewContext(["kcap-review"]) with { Work = WorkLocation.OwnedWorktree }, cts.Token)
            .WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Read file"}""",
            optionsJson: """[{"optionId":"allow_once","name":"Allow once","kind":"allow_once"}]""");
        await started.Runtime.SendUserInputAsync("review").WaitAsync(HangGuard);

        await Assert.That(process.HasExited).IsFalse();
        await Assert.That(connection.RequestAcpInteractionAsyncCalled).IsFalse();

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>A NON-review launch is unaffected: interaction stays Disabled (routed to a human),
    /// which is what keeps an interactive session interactive.</summary>
    [Test]
    public async Task NonReviewLaunch_KeepsInteractionDisabled() {
        await Assert.That(AcpHostedAgentRuntimeFactory.ResolveUnattendedInteractionPolicy(
                MakeContext("agent-1") with { IsBorrowedSnapshot = true },
                AcpVendorDescriptors.Copilot))
            .IsEqualTo(AcpUnattendedInteractionPolicy.Disabled);
    }

    /// <summary>The sandbox is applied at the spawn, not merely described by the policy: the borrowed
    /// launch runs <c>sandbox-exec</c> with the profile, and the vendor binary becomes its argument.
    ///
    /// <para>Asserting only that the entry sets <c>RequiresProcessSandbox</c> would pass against a
    /// builder that read the flag and did nothing with it — the reviewer would then get the widened
    /// read tools with no boundary at all, which is strictly worse than before this change.</para></summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_BorrowedSnapshot_SpawnsUnderTheSandbox() {
        var ctx = ReviewContext(["kcap-review"]) with {
            Work = WorkLocation.OwnedWorktree, IsBorrowedSnapshot = true
        };
        var supported = CopilotBorrowedReviewPolicy.Resolve(
            OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);

        // A REAL executable, because the builder now resolves the configured value through PATH before
        // drawing the profile — a fictional path fails closed, which is asserted separately below.
        var realBinary = Environment.ProcessPath!;

        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Copilot, new DaemonConfig { CopilotPath = realBinary },
            ctx, supported, BrokeredEnv());
        var argv = psi.ArgumentList.ToArray();

        await Assert.That(psi.FileName).IsEqualTo(BorrowedReviewSandbox.SandboxExecPath);
        await Assert.That(argv[0]).IsEqualTo("-p");
        await Assert.That(argv[1]).Contains("(deny default)");
        // The snapshot the reviewer was given is the one the profile grants.
        await Assert.That(argv[1]).Contains($"(subpath \"{ctx.Worktree.Path}\")");
        // The program under the sandbox is the RESOLVED absolute path, so what is granted and what runs
        // are the same file.
        await Assert.That(argv[2]).IsEqualTo(Path.GetFullPath(realBinary));
        // The vendor argv survives the wrap intact — the read tools are still there.
        foreach (var readTool in CopilotBorrowedReviewPolicy.ReadToolIds)
            await Assert.That(argv).Contains($"--available-tools={readTool}");
    }

    /// <summary>An unresolvable vendor path fails closed instead of being sandboxed by guesswork.
    ///
    /// <para>This is the other half of resolving through PATH. The severe route review found is that
    /// every vendor path defaults to a bare command name (<c>"copilot"</c>), which
    /// <see cref="Path.GetFullPath(string)"/> resolves against the DAEMON'S CURRENT DIRECTORY — so the
    /// profile would have granted that directory recursively while <c>sandbox-exec</c> ran the real
    /// binary from PATH. Resolving first fixes the common case; refusing an unresolvable value is what
    /// stops the builder inventing a path when resolution fails.</para></summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_BorrowedSnapshot_UnresolvableBinary_FailsClosed() {
        var ctx = ReviewContext(["kcap-review"]) with {
            Work = WorkLocation.OwnedWorktree, IsBorrowedSnapshot = true
        };
        var supported = CopilotBorrowedReviewPolicy.Resolve(
            OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Copilot,
                new DaemonConfig { CopilotPath = "/definitely/not/an/executable/copilot" },
                ctx, supported, BrokeredEnv()));

        await Assert.That(ex.Message).Contains("borrowed_review_vendor_binary_unresolved");
    }

    /// <summary>A nested borrowed cwd is sandboxed at the snapshot ROOT, not at the subdirectory the
    /// reviewer happens to start in.
    ///
    /// <para>When the requester's cwd is below the repository root, the daemon's snapshot carries
    /// <c>Path = &lt;snapshot&gt;/&lt;relative-cwd&gt;</c> and <c>SnapshotRoot = &lt;snapshot&gt;</c>.
    /// Drawing the boundary at <c>Path</c> leaves the reviewer unable to read the snapshot's parent
    /// files or its root <c>.git</c> — which is the original blind-review defect returning for a
    /// perfectly ordinary launch shape (`kcap` invoked from `repo/src`). The root-equals-cwd test
    /// above cannot catch it, because there the two are the same string.</para></summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_BorrowedSnapshot_NestedCwd_SandboxesTheSnapshotRoot() {
        var ctx = ReviewContext(["kcap-review"]) with {
            Work = WorkLocation.OwnedWorktree,
            IsBorrowedSnapshot = true,
            Worktree = new WorktreeInfo(
                Path:         "/snap/borrowed-abc/src/nested",
                Branch:       "b",
                SourceRepo:   "/repo",
                SnapshotRoot: "/snap/borrowed-abc")
        };
        var supported = CopilotBorrowedReviewPolicy.Resolve(
            OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);

        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Copilot, ResolvableConfig(), ctx, supported, BrokeredEnv());
        var profile = psi.ArgumentList[1];

        await Assert.That(profile).Contains("(subpath \"/snap/borrowed-abc\")");
        // ...and the reviewer still STARTS in the nested cwd it was given.
        await Assert.That(psi.WorkingDirectory).IsEqualTo("/snap/borrowed-abc/src/nested");
        // The per-launch state root is a sibling of the snapshot root, so a per-round refresh of the
        // snapshot neither destroys the running vendor's state nor exposes it as content under review.
        await Assert.That(psi.Environment["HOME"]!.StartsWith("/snap/borrowed-abc.vendor-state",
                                                             StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>The credential gate at the spawn boundary. Defense in depth: the policy already
    /// refuses to advertise borrowed review without a brokerable credential, so reaching here without
    /// one means the daemon's environment changed under a resolved entry.
    ///
    /// <para>Failing is the only correct answer. The profile deliberately no longer grants
    /// <c>~/Library/Keychains</c>, so a reviewer spawned without a token cannot authenticate — it would
    /// burn a daemon slot, stall the round and report nothing useful. Failing before a child exists,
    /// with a reason naming the variables to set, is strictly better.</para></summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_BorrowedSnapshot_WithoutABrokeredToken_FailsClosed() {
        var ctx = ReviewContext(["kcap-review"]) with {
            Work = WorkLocation.OwnedWorktree, IsBorrowedSnapshot = true
        };
        var supported = CopilotBorrowedReviewPolicy.Resolve(
            OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Copilot, new DaemonConfig(), ctx, supported,
                readEnvironmentVariable: _ => null));

        await Assert.That(ex.Message).Contains("borrowed_review_auth_unavailable");
        // Actionable: an operator reading this must learn WHICH variables would fix it.
        foreach (var variable in BorrowedReviewAuthBroker.SourceVariables)
            await Assert.That(ex.Message).Contains(variable);
    }

    /// <summary>The brokered token reaches the child, and the reviewer's HOME and TMPDIR are moved
    /// into the per-launch state root.
    ///
    /// <para>All three are what replaced the profile's grants of <c>~/Library/Keychains</c>,
    /// <c>~/.copilot</c> and <c>~/Library/Caches/copilot</c>. Asserting the profile no longer NAMES
    /// those paths is not enough on its own: a launch that narrowed the profile but left the vendor
    /// pointed at the real home would simply fail to start, and one that left it pointed at the real
    /// home while widening the profile back would be the original hole.</para></summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_BorrowedSnapshot_RedirectsVendorStateAndBrokersTheToken() {
        var ctx = ReviewContext(["kcap-review"]) with {
            Work = WorkLocation.OwnedWorktree, IsBorrowedSnapshot = true,
            Worktree = new WorktreeInfo(Path: "/snap/b1", Branch: "b", SourceRepo: "/repo",
                                        SnapshotRoot: "/snap/b1")
        };
        var supported = CopilotBorrowedReviewPolicy.Resolve(
            OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);

        var env = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Copilot, ResolvableConfig(), ctx, supported,
            readEnvironmentVariable: name => name == "GH_TOKEN" ? "brokered-value" : null).Environment;

        await Assert.That(env[BorrowedReviewAuthBroker.TargetVariable]).IsEqualTo("brokered-value");
        await Assert.That(env["HOME"]).IsEqualTo(Path.Combine("/snap/b1.vendor-state", "home"));
        await Assert.That(env["TMPDIR"]).IsEqualTo(Path.Combine("/snap/b1.vendor-state", "tmp"));
        // The user's real home must not leak in through either variable.
        await Assert.That(env["HOME"]).IsNotEqualTo(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>The paired direction: a NON-borrowed launch gets no state redirection and no brokered
    /// token. An interactive agent runs as the user, with the user's own vendor profile and
    /// credentials — redirecting those would break it, and doing so silently would be worse.</summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_NonBorrowedReview_LeavesTheEnvironmentAlone() {
        var ctx = ReviewContext(["kcap-review"]) with { Work = WorkLocation.OwnedWorktree };
        var supported = CopilotBorrowedReviewPolicy.Resolve(
            OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);

        var env = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Copilot, new DaemonConfig(), ctx, supported,
            readEnvironmentVariable: _ => "would-be-token").Environment;

        // Asserted by absence of VALUE, not of key: ProcessStartInfo.Environment starts as a copy of
        // the current process's environment, so HOME and TMPDIR keys are already present and a
        // key-absence assertion would pass vacuously while the redirection leaked in.
        await Assert.That(env.Values.Any(v => v == "would-be-token")).IsFalse();
        await Assert.That(env.Values.Any(v => v is not null && v.Contains(
            Capacitor.Cli.Daemon.Services.WorktreeManager.VendorStateSuffix, StringComparison.Ordinal))).IsFalse();

        if (Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } ambientHome)
            await Assert.That(env["HOME"]).IsEqualTo(ambientHome);
    }

    /// <summary>The paired direction for the sandbox: a NON-borrowed review spawns the vendor binary
    /// directly. Wrapping every launch would work and would be wrong — it would put an unnecessary
    /// deprecated dependency on the ordinary path and mask a regression in the borrowed one.</summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_NonBorrowedReview_SpawnsTheVendorDirectly() {
        var ctx = ReviewContext(["kcap-review"]) with { Work = WorkLocation.OwnedWorktree };
        var supported = CopilotBorrowedReviewPolicy.Resolve(
            OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);

        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Copilot, new DaemonConfig { CopilotPath = "/opt/bin/copilot" },
            ctx, supported);

        await Assert.That(psi.FileName).IsEqualTo("/opt/bin/copilot");
        await Assert.That(psi.ArgumentList.Any(a => a == "-p")).IsFalse();
    }

    /// <summary>The paired direction, and the one most easily lost: a NON-borrowed Copilot review
    /// (owned worktree, or context-only) keeps the flow-result-only clamp and gets no read tools.
    ///
    /// <para>That asymmetry is what makes the server's shipped read-blind rejection correct rather
    /// than paranoid — it rejects precisely because a non-borrowed Copilot cannot read. Asserting
    /// only the borrowed direction would pass against a build that widened every launch.</para></summary>
    [Test]
    public async Task BuildProcessStartInfo_Copilot_NonBorrowedReview_KeepsTheFlowResultOnlyClamp() {
        var ctx = ReviewContext(["kcap-review"]) with { Work = WorkLocation.OwnedWorktree };

        var supported = CopilotBorrowedReviewPolicy.Resolve(OSPlatform.OSX, Architecture.Arm64, sandboxAvailable: true, authBrokerAvailable: () => true);
        var argv = AcpHostedAgentRuntimeFactory
            .BuildProcessStartInfo(AcpVendorDescriptors.Copilot, new DaemonConfig(), ctx, supported)
            .ArgumentList.ToArray();

        await Assert.That(argv).Contains("--available-tools=kcap-flow-result-submit_review_result");

        foreach (var readTool in CopilotBorrowedReviewPolicy.ReadToolIds)
            await Assert.That(argv).DoesNotContain($"--available-tools={readTool}");
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

    // ── Trust-by-default borrowed-snapshot launch ─────────────────────────────────────────────
    // docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md
    //
    // A borrowed-snapshot Cursor launch used to resolve its binary through — and be re-gated on — an
    // exact-build record, so an ordinary Cursor auto-update turned the launch into a hard
    // `cursor_borrowed_artifact_not_certified` throw. Capability is now advertised for whatever
    // build is installed, and the launch path must agree with that advertisement.

    /// <summary>A snapshot-materialized borrowed review flow: <c>Work</c> is the daemon-owned
    /// snapshot worktree (what the orchestrator hands the factory) with the borrowed-snapshot
    /// marker set.</summary>
    static RuntimeStartContext BorrowedSnapshotContext() =>
        ReviewContext() with { Work = WorkLocation.OwnedWorktree, IsBorrowedSnapshot = true };

    /// <summary>A daemon environment carrying a brokered credential.
    ///
    /// <para>A sandboxed borrowed launch fails closed without one — the profile does not grant the
    /// keychain, so a reviewer with no token cannot authenticate and there is no point spawning it.
    /// Tests that assert the borrowed ARGV supply this so they exercise the argv rather than the
    /// credential gate, and so they behave identically on a developer machine and on CI. The gate
    /// itself is asserted separately, by
    /// <see cref="BuildProcessStartInfo_Copilot_BorrowedSnapshot_WithoutABrokeredToken_FailsClosed"/>.</para></summary>
    static Func<string, string?> BrokeredEnv() =>
        name => name == BorrowedReviewAuthBroker.TargetVariable ? "test-token" : null;

    /// <summary>A config whose vendor path is a REAL, resolvable executable.
    ///
    /// <para>A borrowed-snapshot launch resolves the configured value through PATH before drawing the
    /// sandbox, and fails closed when it cannot. The shipped default is the bare name <c>"copilot"</c>,
    /// so any borrowed-argv test using <c>new DaemonConfig()</c> passes only on a machine that happens
    /// to have Copilot installed — green locally, red on CI, which is exactly what happened. The test
    /// host's own binary is guaranteed to exist and be executable everywhere.</para></summary>
    static DaemonConfig ResolvableConfig() => new() { CopilotPath = Environment.ProcessPath! };

    /// <summary>The borrowed-snapshot binary is the plainly configured one — the same path every
    /// other vendor and every other launch shape uses. A configured path that matches no validated
    /// build (this one resolves to nothing at all) must still be spawned verbatim.</summary>
    [Test]
    public async Task BuildProcessStartInfo_Cursor_BorrowedSnapshot_UsesTheConfiguredBinaryPath() {
        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Cursor,
            new DaemonConfig { CursorPath = "/definitely/not/a/validated/build/cursor-agent" },
            BorrowedSnapshotContext());

        await Assert.That(psi.FileName).IsEqualTo("/definitely/not/a/validated/build/cursor-agent");
        await Assert.That(psi.ArgumentList.SequenceEqual(["acp", "--force", "--approve-mcps", "--trust"])).IsTrue();
    }

    /// <summary>THE launch-side regression test: a borrowed Cursor reviewer on a build that matches
    /// no validated-build record SPAWNS instead of throwing
    /// <c>cursor_borrowed_artifact_not_certified</c>.</summary>
    [Test]
    public async Task ReviewFlow_Cursor_BorrowedSnapshot_OnANonMatchingBuild_Spawns() {
        var fake    = new FakeAcpAgent();
        var spawns  = 0;
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "/definitely/not/a/validated/build/cursor-agent" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: new CaptureServerConnection(),
            connectionSource: _ => {
                Interlocked.Increment(ref spawns);
                return (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess());
            });

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await factory.StartAsync(BorrowedSnapshotContext(), cts.Token).WaitAsync(HangGuard);

        await Assert.That(Volatile.Read(ref spawns)).IsEqualTo(1);

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>The gate that REMAINS: a snapshot-materialized launch handed to a vendor that does
    /// not declare independent-snapshot containment is a wiring bug, and still fails before spawn.
    ///
    /// <para>Copilot used to be the subject here, because its descriptor declared native-tool-clamp.
    /// It no longer is: on a verified platform Copilot resolves to independent-snapshot, so pointing
    /// this test at Copilot would assert the mismatch on an unverified host and DEADLOCK on a real
    /// handshake on a verified one — a host-dependent test either way. A synthetic descriptor that
    /// declares the mismatch outright keeps the gate covered on every platform.</para></summary>
    [Test]
    public async Task ReviewFlow_BorrowedSnapshot_ContainmentMismatch_StillThrowsBeforeSpawn() {
        var mismatched = SyntheticDescriptor(
            supportsMcpServers: true,
            borrowedReview:     true,
            containment:        AcpBorrowedReviewContainment.NativeToolClamp);
        var (factory, spawns) = CountingSpawnFactory(mismatched);

        var ex = await Assert.That(async () => await factory.StartAsync(
                ReviewContext(["kcap-review"]) with { Work = WorkLocation.OwnedWorktree, IsBorrowedSnapshot = true },
                CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(ex!.Message).IsEqualTo("borrowed_snapshot_containment_mismatch");
        await Assert.That(spawns()).IsEqualTo(0);
    }

    /// <summary>NEGATIVE test: a launch logs no vendor version and no resolved binary path. Adding
    /// either would be per-launch drift telemetry, which this design rejects — resolving a
    /// meaningful path would require final-symlink inspection added specifically to reveal version
    /// drift, and it would still be racy. A test demanding such a line would reintroduce the
    /// rejected behavior.</summary>
    [Test]
    public async Task StartAsync_BorrowedSnapshot_LogsNoVendorVersionAndNoBinaryPath() {
        var fake          = new FakeAcpAgent();
        var loggerFactory = new CaptureLoggerFactory();
        var binaryPath    = "/definitely/not/a/validated/build/cursor-agent";

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = binaryPath },
            loggerFactory: loggerFactory,
            connection: new CaptureServerConnection(),
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()));

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await factory.StartAsync(BorrowedSnapshotContext(), cts.Token).WaitAsync(HangGuard);

        foreach (var (_, message) in loggerFactory.Logger.Entries) {
            await Assert.That(message.Contains(binaryPath, StringComparison.Ordinal)).IsFalse();
            await Assert.That(message.Contains("cursor-agent", StringComparison.Ordinal)).IsFalse();
            await Assert.That(message.Contains(CursorBorrowedReviewValidation.Version, StringComparison.Ordinal)).IsFalse();
        }

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
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

    // ── the Gemini reviewer's launch identity is not a caller input ──

    /// <summary>
    /// StartAsync overwrites any <c>LaunchIdentity</c> supplied on the way in. Honouring one would let a
    /// requester choose the names whose unguessability is the entire MCP containment for an aliasing vendor,
    /// so this asserts the launch does not use a caller-chosen value rather than trusting that no caller sets
    /// one.
    /// </summary>
    [Test]
    public async Task Gemini_ACallerSuppliedLaunchIdentity_DoesNotReachTheSpawnSeam() {
        var attacker = LaunchIdentity.FromGuids(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            aliasResultChannel: true);

        var seen = (LaunchIdentity?)null;
        var fake = new FakeAcpAgent();
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Gemini,
            config: new DaemonConfig { GeminiUnattendedReviewerEnabled = true },
            loggerFactory: NullLoggerFactory.Instance,
            connection: new CaptureServerConnection(),
            connectionSource: ctx => {
                seen = ctx.LaunchIdentity;
                return (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess());
            },
            // PINNED. Since the capability gate moved ahead of the connection source, StartAsync resolves a
            // version before this seam is reached — so without pinning, this test depends on whether a
            // certified gemini happens to be installed: green on a dev machine, red on CI where the gate
            // refuses as version-unresolved and `seen` stays null. Review caught exactly that.
            resolveVendorVersion: _ => GeminiReviewerCapability.CertifiedVersions.First());

        var ctx = ReviewContext() with { Vendor = "gemini", LaunchIdentity = attacker };

        // A bare FakeAcpAgent never answers `initialize`, so StartAsync would wait for a handshake that
        // never arrives. The subject is what the SPAWN SEAM was handed, which is recorded before any frame is
        // exchanged — so a short-lived token is enough and keeps the test from hanging the suite.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await factory.StartAsync(ctx, cts.Token); }
        catch { /* the handshake is not the subject; the seam recorded what it was handed either way */ }

        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.ResultChannelWireName).IsNotEqualTo(attacker.ResultChannelWireName);
        await Assert.That(seen.UnmatchableMcpName).IsNotEqualTo(attacker.UnmatchableMcpName);
    }

    /// <summary>
    /// Advertisement respects the operator's capability gate, so a daemon that never opted in is not selected
    /// as a Gemini reviewer host at all. The optimisation arm — the boundary is the pre-spawn check in
    /// BuildProcessStartInfo — but advertising a capability the daemon will then refuse is its own defect.
    /// </summary>
    [Test]
    public async Task Gemini_ADisabledDaemon_DoesNotAdvertiseUnattendedSupport() {
        // The version resolver is PINNED to a certified value so the only thing that can make this false is
        // the operator flag. Review caught the earlier version leaving it unpinned: on a host with no gemini
        // the version is unknown, so the test passed for that reason and would have kept passing if
        // advertisement stopped honouring the flag entirely.
        var certified = GeminiReviewerCapability.CertifiedVersions.First();

        IHostedAgentRuntimeFactory disabled = new AcpHostedAgentRuntimeFactory(
            AcpVendorDescriptors.Gemini, new DaemonConfig(), NullLoggerFactory.Instance,
            new CaptureServerConnection(), resolveVendorVersion: _ => certified);

        await Assert.That(disabled.SupportsUnattended).IsFalse();

        IHostedAgentRuntimeFactory enabled = new AcpHostedAgentRuntimeFactory(
            AcpVendorDescriptors.Gemini, new DaemonConfig { GeminiUnattendedReviewerEnabled = true },
            NullLoggerFactory.Instance, new CaptureServerConnection(),
            resolveVendorVersion: _ => certified);

        await Assert.That(enabled.SupportsUnattended).IsTrue()
            .Because("the positive control: without it, an advertisement that always said false would pass");
    }

    /// <summary>An enabled daemon on an UNCERTIFIED build still does not advertise — the two halves of the
    /// gate are independent, and this is the half a version bump would break.</summary>
    [Test]
    public async Task Gemini_AnEnabledDaemonOnAnUncertifiedVersion_DoesNotAdvertise() {
        IHostedAgentRuntimeFactory factory = new AcpHostedAgentRuntimeFactory(
            AcpVendorDescriptors.Gemini, new DaemonConfig { GeminiUnattendedReviewerEnabled = true },
            NullLoggerFactory.Instance, new CaptureServerConnection(),
            resolveVendorVersion: _ => "99.99.99");

        await Assert.That(factory.SupportsUnattended).IsFalse();
    }

    /// <summary>Other vendors' advertisement is unaffected — the gate is Gemini-scoped.</summary>
    [Test]
    public async Task OtherVendorsAdvertisement_IsUnaffectedByTheGeminiGate() {
        IHostedAgentRuntimeFactory cursor = new AcpHostedAgentRuntimeFactory(
            AcpVendorDescriptors.Cursor, new DaemonConfig(), NullLoggerFactory.Instance,
            new CaptureServerConnection());

        await Assert.That(cursor.SupportsUnattended).IsTrue();
    }

    /// <summary>
    /// The capability gate must be reached BEFORE any connection source runs — including a supplied one.
    ///
    /// <para>Review found the gate lived only in <c>BuildProcessStartInfo</c>, which only the DEFAULT source
    /// calls, so a supplied source was invoked for a disabled daemon and could spawn directly. A test seam is
    /// still a bypass of the claimed invariant, so this asserts the source is never even called.</para>
    /// </summary>
    [Test]
    public async Task Gemini_ADisabledDaemon_NeverReachesASuppliedConnectionSource() {
        var reached = 0;
        var fake    = new FakeAcpAgent();
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Gemini,
            config: new DaemonConfig(),                       // NOT enabled
            loggerFactory: NullLoggerFactory.Instance,
            connection: new CaptureServerConnection(),
            connectionSource: _ => {
                Interlocked.Increment(ref reached);
                return (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess());
            },
            resolveVendorVersion: _ => GeminiReviewerCapability.CertifiedVersions.First());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.That(async () => await factory.StartAsync(
                ReviewContext() with { Vendor = "gemini" }, cts.Token))
            .Throws<InvalidOperationException>();

        await Assert.That(Volatile.Read(ref reached)).IsEqualTo(0)
            .Because("the gate is the boundary, so nothing may spawn — not even a supplied source");
    }

    /// <summary>The positive control: an ENABLED daemon on a certified build does reach the source, so the
    /// test above cannot pass because StartAsync fails for some unrelated reason.</summary>
    [Test]
    public async Task Gemini_AnEnabledDaemon_DoesReachTheConnectionSource() {
        var reached = 0;
        var fake    = new FakeAcpAgent();
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Gemini,
            config: new DaemonConfig { GeminiUnattendedReviewerEnabled = true },
            loggerFactory: NullLoggerFactory.Instance,
            connection: new CaptureServerConnection(),
            connectionSource: _ => {
                Interlocked.Increment(ref reached);
                return (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess());
            },
            resolveVendorVersion: _ => GeminiReviewerCapability.CertifiedVersions.First());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await factory.StartAsync(ReviewContext() with { Vendor = "gemini" }, cts.Token); }
        catch { /* the handshake is not the subject — a bare fake never answers initialize */ }

        await Assert.That(Volatile.Read(ref reached)).IsEqualTo(1);
    }

    /// <summary>
    /// THE identity-threading invariant, asserted end to end across the two ACTUAL sinks of one real
    /// <see cref="AcpHostedAgentRuntimeFactory.StartAsync"/> run: the serialized <c>session/new.mcpServers</c>
    /// payload the vendor received, and the context the spawn seam was handed.
    ///
    /// <para>Review's point, and it was right twice: the earlier versions compared two fixture contexts, or a
    /// fixture context against a helper, so a regression that regenerated the identity BETWEEN building the
    /// MCP list and invoking the connection source would leave every other test green. That regression is the
    /// exact silent failure the type exists to prevent — a reviewer whose allowlist does not admit its own
    /// result channel starts normally and can never report.</para>
    /// </summary>
    [Test]
    public async Task Gemini_TheSessionNewChannelName_MatchesTheIdentityHandedToTheSpawnSeam() {
        var fake = new FakeAcpAgent();
        RuntimeStartContext? atSeam = null;

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Gemini,
            config: new DaemonConfig { GeminiUnattendedReviewerEnabled = true },
            loggerFactory: NullLoggerFactory.Instance,
            connection: new CaptureServerConnection(),
            connectionSource: ctx => {
                atSeam = ctx;
                return (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess());
            },
            resolveVendorVersion: _ => GeminiReviewerCapability.CertifiedVersions.First());

        // fake.RunAsync is what serves the handshake — without it StartAsync waits on `initialize` and the
        // token cancels before session/new is ever sent.
        using var cts = new CancellationTokenSource();
        var fakeRun = fake.RunAsync(cts.Token);

        var started = await factory.StartAsync(ReviewContext() with { Vendor = "gemini" }, cts.Token)
                                   .WaitAsync(HangGuard, cts.Token);

        var mcpJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(atSeam).IsNotNull();
        var wire = atSeam!.LaunchIdentity!.ResultChannelWireName;

        // The wire name the spawn seam was handed must be the name that actually crossed the wire — read from
        // the serialized payload, not re-derived.
        await Assert.That(mcpJson).Contains($"\"{wire}\"")
            .Because("the injected channel must carry the SAME identity the spawn seam got; two derivations "
                   + "produce a reviewer whose allowlist does not admit its own channel, and it fails silently");

        // And it must be an alias, not the canonical id — otherwise this would pass trivially for a
        // non-aliasing vendor and prove nothing about Gemini.
        await Assert.That(wire).IsNotEqualTo(KcapMcpRegistry.ReservedResultChannelId);
        await Assert.That(mcpJson).DoesNotContain($"\"{KcapMcpRegistry.ReservedResultChannelId}\"");

        cts.Cancel();
        try { await fakeRun.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }
}
