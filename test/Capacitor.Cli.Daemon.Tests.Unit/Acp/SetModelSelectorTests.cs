using System.Text.Json;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// Unit-tests <see cref="SetModelSelector.TrySelectAsync"/> directly against a
/// <see cref="FakeAcpAgent"/>-backed <see cref="AcpConnection"/>, mirroring
/// <see cref="ConfigOptionModelSelectorTests"/> case-for-case: the two selectors share the
/// resolution half (guard → parse <c>session/new.models</c> → <see
/// cref="Capacitor.Cli.Core.Acp.AcpModelResolver"/>) and differ only in the wire write —
/// <c>session/set_model {sessionId, modelId}</c> here versus
/// <c>session/set_config_option {sessionId, configId, value}</c> there — plus one case the
/// sibling covers elsewhere: a JSON-RPC ERROR response to the write must be swallowed into
/// <see langword="null"/> (model selection is never a launch precondition), while a canceled
/// <c>ct</c> must propagate <see cref="OperationCanceledException"/> uncaught.
/// </summary>
public class SetModelSelectorTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>Records every log call — used to assert a Warning was (or wasn't) logged.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    sealed class Harness : IAsyncDisposable {
        public FakeAcpAgent            Fake { get; } = new();
        public AcpConnection           Conn { get; }
        public CaptureLogger           Logger { get; } = new();
        public CancellationTokenSource Cts  { get; } = new();

        Task _fakeRunTask = Task.CompletedTask;

        public Harness() => Conn = new AcpConnection(Fake.ClientWriteStream, Fake.ClientReadStream, NullLogger.Instance);

        public void StartFakeAgentLoop() {
            _fakeRunTask = Fake.RunAsync(Cts.Token);
            _ = Conn.RunAsync(Cts.Token);
        }

        public async ValueTask DisposeAsync() {
            Cts.Cancel();
            try {
                await _fakeRunTask.WaitAsync(HangGuard);
            } catch (OperationCanceledException) {
                // expected shutdown path
            }
            await Conn.DisposeAsync();
            await Fake.DisposeAsync();
            Cts.Dispose();
        }
    }

    // The probe-measured Kiro shape: bare ids, modelId == name, no parameterized variants.
    static readonly (string ModelId, string Name)[] AvailableModels = [
        ("auto", "auto"),
        ("claude-haiku-4.5", "claude-haiku-4.5"),
        ("deepseek-3.2", "deepseek-3.2"),
    ];

    // (a) no requested model → returns null, no session/set_model sent.
    [Test]
    public async Task TrySelectAsync_NoRequestedModel_ReturnsNull_NoRpcSent() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var sessionNewResult = FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "auto", AvailableModels);

        var result = await SetModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: null, h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsNull();
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_model")).IsFalse();
    }

    // (b) a resolvable model → sends session/set_model with the resolved id, returns it.
    [Test]
    public async Task TrySelectAsync_ResolvableModel_SendsSetModel_ReturnsResolvedId() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var sessionNewResult = FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "auto", AvailableModels);

        var result = await SetModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "deepseek-3.2", h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsEqualTo("deepseek-3.2");

        var call = h.Fake.ReceivedCalls.Single(c => c.Method == "session/set_model");
        await Assert.That(call.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo(FakeAcpAgent.FixedSessionId);
        await Assert.That(call.Params!.Value.GetProperty("modelId").GetString()).IsEqualTo("deepseek-3.2");
        // session/set_model carries the id under "modelId" — never set_config_option's
        // {configId, value} pair.
        await Assert.That(call.Params!.Value.TryGetProperty("configId", out _)).IsFalse();
        await Assert.That(call.Params!.Value.TryGetProperty("value", out _)).IsFalse();
    }

    // (c) an unresolvable model (not in availableModels) → returns null, no RPC sent, a Warning logged.
    [Test]
    public async Task TrySelectAsync_UnresolvableModel_ReturnsNull_NoRpcSent_LogsWarning() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var sessionNewResult = FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "auto", AvailableModels);

        var result = await SetModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "totally-unknown-model", h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsNull();
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_model")).IsFalse();
        await Assert.That(h.Logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("totally-unknown-model"))).IsTrue();
    }

    // (d) session/new's 'models' property absent/malformed → returns null, no throw.
    [Test]
    public async Task TrySelectAsync_ModelsPropertyAbsent_ReturnsNull_NoThrow() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var sessionNewResult = JsonDocument.Parse($$"""{"sessionId":"{{FakeAcpAgent.FixedSessionId}}"}""").RootElement;

        var result = await SetModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "deepseek-3.2", h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsNull();
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_model")).IsFalse();
    }

    [Test]
    public async Task TrySelectAsync_ModelsPropertyMalformed_ReturnsNull_NoThrow() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var sessionNewResult = JsonDocument.Parse($$"""{"sessionId":"{{FakeAcpAgent.FixedSessionId}}","models":"not-an-object"}""").RootElement;

        var result = await SetModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "deepseek-3.2", h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsNull();
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_model")).IsFalse();
    }

    // (e) a JSON-RPC ERROR response to session/set_model → swallowed to null + Warning, never fatal
    // (the "model selection is a nice-to-have, never a launch precondition" contract).
    [Test]
    public async Task TrySelectAsync_SetModelErrorResponse_ReturnsNull_LogsWarning() {
        await using var h = new Harness();
        h.Fake.FailNextSetModel();
        h.StartFakeAgentLoop();

        var sessionNewResult = FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "auto", AvailableModels);

        var result = await SetModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "deepseek-3.2", h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsNull();
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_model")).IsTrue();
        await Assert.That(h.Logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("deepseek-3.2"))).IsTrue();
    }

    // (f) a ct canceled WHILE session/set_model is in flight — TrySelectAsync must let
    // OperationCanceledException propagate out, never returning null for a cancellation the way it
    // does for a resolution failure (the IAcpModelSelector cancellation contract).
    [Test]
    public async Task TrySelectAsync_CanceledWhileSetModelInFlight_PropagatesOperationCanceled() {
        await using var h = new Harness();
        h.Fake.HoldSetModelResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.StartFakeAgentLoop();

        var sessionNewResult = FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "auto", AvailableModels);

        using var innerCts = new CancellationTokenSource();

        var selectTask = SetModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "deepseek-3.2", h.Logger, innerCts.Token);

        // Wait for the RPC to actually be in flight (recorded by the fake) before cancelling.
        var deadline = DateTime.UtcNow + HangGuard;
        while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_model") && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_model")).IsTrue();

        await innerCts.CancelAsync();

        await Assert.That(async () => await selectTask.WaitAsync(HangGuard)).Throws<OperationCanceledException>();

        // Release the fake's held response so the harness can tear down cleanly.
        h.Fake.HoldSetModelResponse.TrySetResult();
    }

    [Test]
    public async Task CanSelectModel_IsTrue() {
        await Assert.That(SetModelSelector.Instance.CanSelectModel).IsTrue();
    }
}
