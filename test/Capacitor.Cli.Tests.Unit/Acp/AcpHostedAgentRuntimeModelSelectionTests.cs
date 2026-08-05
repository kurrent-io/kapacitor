// test/Capacitor.Cli.Tests.Unit/Acp/AcpHostedAgentRuntimeModelSelectionTests.cs
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Model-selection gap 1: exercises <see cref="AcpHostedAgentRuntime.StartAsync"/>'s model-selection step —
/// <c>session/set_config_option</c>, sent AFTER <c>session/new</c> resolves and BEFORE the first
/// <c>session/prompt</c> fires — end-to-end against <see cref="FakeAcpAgent"/>. Mirrors the
/// <c>AcpHostedAgentRuntimeTests</c> harness pattern; no real <c>cursor-agent acp</c> process.
/// </summary>
public class AcpHostedAgentRuntimeModelSelectionTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>Minimal <see cref="IAcpProcess"/> stand-in — these tests never exercise process exit/terminate.</summary>
    sealed class FakeAcpProcess : IAcpProcess {
        public int  Pid       { get; init; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) =>
            timeout is { } t ? Task.Delay(t) : Task.Delay(Timeout.InfiniteTimeSpan);

        public Task TerminateAsync(TimeSpan? timeout = null) {
            HasExited = true;
            ExitCode  = 0;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Harness : IAsyncDisposable {
        public FakeAcpAgent          Fake    { get; }
        public AcpConnection         Conn    { get; }
        public FakeAcpProcess        Process { get; }
        public AcpHostedAgentRuntime Runtime { get; }
        public CancellationTokenSource Cts   { get; } = new();

        Task _fakeRunTask = Task.CompletedTask;

        public Harness(IAcpModelSelector? modelSelector = null) {
            Fake    = new FakeAcpAgent();
            Conn    = new AcpConnection(Fake.ClientWriteStream, Fake.ClientReadStream, NullLogger.Instance);
            Process = new FakeAcpProcess();
            // null keeps the runtime's legacy ConfigOptionModelSelector default, preserving every
            // existing test unchanged; the set_model composition test passes SetModelSelector.
            Runtime = new AcpHostedAgentRuntime(Conn, Process, NullLogger.Instance, modelSelector: modelSelector);
        }

        public void StartFakeAgentLoop() => _fakeRunTask = Fake.RunAsync(Cts.Token);

        public async ValueTask DisposeAsync() {
            Cts.Cancel();
            try {
                await _fakeRunTask.WaitAsync(HangGuard);
            } catch (OperationCanceledException) {
                // expected shutdown path
            }
            await Runtime.DisposeAsync();
            await Fake.DisposeAsync();
            Cts.Dispose();
        }
    }

    // Mirrors the Team-tier availableModels list from docs/ai-688-cursor-prototype-findings.md
    // (trimmed to the entries these tests actually reference).
    static readonly (string ModelId, string Name)[] TeamAvailableModels = [
        ("default[]", "default"),
        ("composer-2.5[fast=true]", "composer-2.5"),
        ("claude-sonnet-4-5[thinking=true,context=200k]", "claude-sonnet-4-5"),
        ("claude-opus-4-8[thinking=true]", "claude-opus-4-8"),
    ];

    static async Task<IReadOnlyList<(string Method, System.Text.Json.JsonElement? Params)>> WaitForCallCountAsync(
            FakeAcpAgent fake, int minCount) {
        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.ReceivedCalls.Count < minCount && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        return fake.ReceivedCalls;
    }

    // The probe-measured Kiro shape (docs/probes/2026-08-05-kiro-model-override/): bare ids,
    // modelId == name.
    static readonly (string ModelId, string Name)[] KiroAvailableModels = [
        ("auto", "auto"),
        ("claude-haiku-4.5", "claude-haiku-4.5"),
        ("deepseek-3.2", "deepseek-3.2"),
    ];

    /// <summary>The set_model composition case: the SAME runtime handshake, carrying
    /// <see cref="SetModelSelector"/> the way the factory passes Kiro's descriptor selector,
    /// sends <c>session/set_model</c> (never <c>session/set_config_option</c>) before the first
    /// prompt and exposes the applied id as <see cref="AcpHostedAgentRuntime.ResolvedModel"/> —
    /// the value registration reports as the live model.</summary>
    [Test]
    public async Task StartAsync_SetModelSelector_SendsSetModelBeforeThePrompt_AndExposesResolvedModel() {
        await using var h = new Harness(modelSelector: SetModelSelector.Instance);
        h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId, currentModelId: "minimax-m2.5", KiroAvailableModels));
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync(
            "/abs/worktree", "do the thing", h.Cts.Token,
            requestedModel: "claude-haiku-4.5"
        ).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(h.Fake, minCount: 4);
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(4);

        await Assert.That(calls[0].Method).IsEqualTo("initialize");
        await Assert.That(calls[1].Method).IsEqualTo("session/new");

        await Assert.That(calls[2].Method).IsEqualTo("session/set_model");
        await Assert.That(calls[2].Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo(FakeAcpAgent.FixedSessionId);
        await Assert.That(calls[2].Params!.Value.GetProperty("modelId").GetString()).IsEqualTo("claude-haiku-4.5");
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option")).IsFalse();

        // The model must be set BEFORE the turn starts, and the applied id is what registration
        // will report as live.
        await Assert.That(calls[3].Method).IsEqualTo("session/prompt");
        await Assert.That(h.Runtime.ResolvedModel).IsEqualTo("claude-haiku-4.5");
    }

    [Test]
    public async Task StartAsync_ExactModelId_SendsSetConfigOptionWithThatExactId_BeforeThePrompt() {
        await using var h = new Harness();
        h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync(
            "/abs/worktree", "do the thing", h.Cts.Token,
            requestedModel: "claude-sonnet-4-5[thinking=true,context=200k]"
        ).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(h.Fake, minCount: 4);
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(4);

        await Assert.That(calls[0].Method).IsEqualTo("initialize");
        await Assert.That(calls[1].Method).IsEqualTo("session/new");

        await Assert.That(calls[2].Method).IsEqualTo("session/set_config_option");
        await Assert.That(calls[2].Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo(FakeAcpAgent.FixedSessionId);
        await Assert.That(calls[2].Params!.Value.GetProperty("configId").GetString()).IsEqualTo("model");
        await Assert.That(calls[2].Params!.Value.GetProperty("value").GetString()).IsEqualTo("claude-sonnet-4-5[thinking=true,context=200k]");

        // The model must be set BEFORE the turn starts.
        await Assert.That(calls[3].Method).IsEqualTo("session/prompt");
    }

    [Test]
    public async Task StartAsync_FamilyPrefixDefault_ResolvesToTheFullParameterizedId() {
        await using var h = new Harness();
        h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        h.StartFakeAgentLoop();

        // "claude-sonnet-4-5" is a bare family prefix (DaemonConfig.CursorModel's default shape),
        // not the exact wire modelId — must resolve to the parameterized id before sending.
        await h.Runtime.StartAsync(
            "/abs/worktree", "do the thing", h.Cts.Token,
            requestedModel: "claude-sonnet-4-5"
        ).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(h.Fake, minCount: 3);
        var setConfigCall = calls.SingleOrDefault(c => c.Method == "session/set_config_option");

        await Assert.That(setConfigCall.Method).IsEqualTo("session/set_config_option");
        await Assert.That(setConfigCall.Params!.Value.GetProperty("value").GetString())
            .IsEqualTo("claude-sonnet-4-5[thinking=true,context=200k]");
    }

    [Test]
    public async Task StartAsync_NoMatchingModel_SkipsSetConfigOption_ButStillFiresThePrompt() {
        await using var h = new Harness();
        h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync(
            "/abs/worktree", "do the thing", h.Cts.Token,
            requestedModel: "totally-unknown-model"
        ).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(h.Fake, minCount: 3);

        await Assert.That(calls.Any(c => c.Method == "session/set_config_option")).IsFalse();
        await Assert.That(calls.Any(c => c.Method == "session/prompt")).IsTrue();
    }

    [Test]
    public async Task StartAsync_NoRequestedModel_SkipsSetConfigOption_ButStillFiresThePrompt() {
        await using var h = new Harness();
        h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        h.StartFakeAgentLoop();

        // No requestedModel argument at all — the pre-existing 3-arg StartAsync call shape must
        // keep working unchanged (back-compat for every other existing call site/test).
        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(h.Fake, minCount: 3);

        await Assert.That(calls.Any(c => c.Method == "session/set_config_option")).IsFalse();
        await Assert.That(calls.Any(c => c.Method == "session/prompt")).IsTrue();
    }

    [Test]
    public async Task StartAsync_JsonRpcErrorFromSetConfigOption_IsNonFatal_PromptStillFires() {
        await using var h = new Harness();
        h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        h.Fake.FailNextSetConfigOption();
        h.StartFakeAgentLoop();

        // Must not throw even though the fake will answer set_config_option with a JSON-RPC error.
        await h.Runtime.StartAsync(
            "/abs/worktree", "do the thing", h.Cts.Token,
            requestedModel: "claude-sonnet-4-5[thinking=true,context=200k]"
        ).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(h.Fake, minCount: 4);

        await Assert.That(calls.Any(c => c.Method == "session/set_config_option")).IsTrue();
        await Assert.That(calls.Any(c => c.Method == "session/prompt")).IsTrue();
    }

    // ── Test-plan item 10: explicit non-fatal (RPC error) vs fatal (cancellation) contrast ──────

    /// <summary>
    /// Test-plan item 10: makes explicit, in ONE place, the contrast this refactor's selector
    /// abstraction must preserve (see <see cref="IAcpModelSelector"/>'s cancellation-contract
    /// remarks) — a JSON-RPC ERROR response to <c>session/set_config_option</c> is a best-effort
    /// RESOLUTION failure (non-fatal: <c>StartAsync</c> completes normally and the turn's
    /// <c>session/prompt</c> still fires), while a CANCELED <c>ct</c> observed during that same
    /// in-flight RPC is NOT a resolution failure — it propagates an
    /// <see cref="OperationCanceledException"/> out of <c>StartAsync</c> and the prompt never fires
    /// at all. Each half is already covered individually (this class's
    /// <see cref="StartAsync_JsonRpcErrorFromSetConfigOption_IsNonFatal_PromptStillFires"/> above,
    /// and the sibling <c>AcpHostedAgentRuntimeTests.StartAsync_CanceledWhileSetConfigOptionInFlight_ThrowsOperationCanceled</c>)
    /// — this test asserts both outcomes side by side, including that the cancelled scenario never
    /// reaches <c>session/prompt</c>, so the contrast itself is pinned in one place.
    /// </summary>
    [Test]
    public async Task StartAsync_RpcErrorIsNonFatal_ButCanceledCtPropagates_ContrastedSideBySide() {
        // ── Scenario A: a JSON-RPC error from set_config_option is a resolution failure — logged,
        // swallowed, never fatal. StartAsync completes and the turn's session/prompt still fires.
        await using (var h = new Harness()) {
            h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
                FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
            h.Fake.FailNextSetConfigOption();
            h.StartFakeAgentLoop();

            await h.Runtime.StartAsync(
                "/abs/worktree", "do the thing", h.Cts.Token,
                requestedModel: "claude-sonnet-4-5[thinking=true,context=200k]"
            ).WaitAsync(HangGuard);

            var calls = await WaitForCallCountAsync(h.Fake, minCount: 4);

            await Assert.That(calls.Any(c => c.Method == "session/set_config_option")).IsTrue();
            await Assert.That(calls.Any(c => c.Method == "session/prompt")).IsTrue();
        }

        // ── Scenario B: ct canceled WHILE set_config_option is in flight is NOT a resolution
        // failure — it propagates out of StartAsync uncaught, and the prompt never fires because
        // StartAsync never reaches the point where it enqueues the initial turn.
        await using (var h = new Harness()) {
            h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
                FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
            h.Fake.HoldSetConfigOptionResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.StartFakeAgentLoop();

            using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(h.Cts.Token);

            var startTask = h.Runtime.StartAsync(
                "/abs/worktree", "do the thing", innerCts.Token,
                requestedModel: "claude-sonnet-4-5[thinking=true,context=200k]"
            );

            // Wait for session/set_config_option to actually be in flight before cancelling.
            var deadline = DateTime.UtcNow + HangGuard;
            while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option") && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option")).IsTrue();

            await innerCts.CancelAsync();

            await Assert.That(async () => await startTask.WaitAsync(HangGuard)).Throws<OperationCanceledException>();
            await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt")).IsFalse();

            // Release the fake's held response so the harness can tear down cleanly.
            h.Fake.HoldSetConfigOptionResponse.TrySetResult();
        }
    }
}
