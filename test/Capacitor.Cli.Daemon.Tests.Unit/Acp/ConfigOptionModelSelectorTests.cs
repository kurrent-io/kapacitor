using System.Text.Json;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Tests.Unit.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// Test plan item 3: unit-tests <see cref="ConfigOptionModelSelector.TrySelectAsync"/>
/// directly against a <see cref="FakeAcpAgent"/>-backed <see cref="AcpConnection"/> — no full
/// <see cref="Capacitor.Cli.Daemon.Services.AcpHostedAgentRuntime"/> needed. Supersedes the
/// equivalent coverage that used to live inline in <see cref="AcpHostedAgentRuntimeModelSelectionTests"/>
/// (that file keeps its existing tests unmodified, since the runtime's legacy null-default
/// modelSelector argument preserves its exact end-to-end behavior); this file adds
/// selector-level unit coverage the old inline-only design couldn't isolate.
/// </summary>
public class ConfigOptionModelSelectorTests {
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

    static readonly (string ModelId, string Name)[] AvailableModels = [
        ("composer-2.5[fast=true]", "composer-2.5"),
        ("claude-sonnet-4-5[thinking=true,context=200k]", "claude-sonnet-4-5"),
    ];

    // (a) no requested model → returns null, no session/set_config_option sent.
    [Test]
    public async Task TrySelectAsync_NoRequestedModel_ReturnsNull_NoRpcSent() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var sessionNewResult = FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "composer-2.5[fast=true]", AvailableModels);

        var result = await ConfigOptionModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: null, h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsNull();
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option")).IsFalse();
    }

    // (b) a resolvable model → sends session/set_config_option with the resolved id, returns it.
    [Test]
    public async Task TrySelectAsync_ResolvableModel_SendsSetConfigOption_ReturnsResolvedId() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var sessionNewResult = FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "composer-2.5[fast=true]", AvailableModels);

        var result = await ConfigOptionModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "claude-sonnet-4-5", h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsEqualTo("claude-sonnet-4-5[thinking=true,context=200k]");

        var call = h.Fake.ReceivedCalls.Single(c => c.Method == "session/set_config_option");
        await Assert.That(call.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo(FakeAcpAgent.FixedSessionId);
        await Assert.That(call.Params!.Value.GetProperty("configId").GetString()).IsEqualTo("model");
        await Assert.That(call.Params!.Value.GetProperty("value").GetString()).IsEqualTo("claude-sonnet-4-5[thinking=true,context=200k]");
    }

    // (c) an unresolvable model (not in availableModels) → returns null, no RPC sent, a Warning logged.
    [Test]
    public async Task TrySelectAsync_UnresolvableModel_ReturnsNull_NoRpcSent_LogsWarning() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var sessionNewResult = FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "composer-2.5[fast=true]", AvailableModels);

        var result = await ConfigOptionModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "totally-unknown-model", h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsNull();
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option")).IsFalse();
        await Assert.That(h.Logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("totally-unknown-model"))).IsTrue();
    }

    // (d) session/new's 'models' property absent/malformed → returns null, no throw.
    [Test]
    public async Task TrySelectAsync_ModelsPropertyAbsent_ReturnsNull_NoThrow() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var sessionNewResult = JsonDocument.Parse($$"""{"sessionId":"{{FakeAcpAgent.FixedSessionId}}"}""").RootElement;

        var result = await ConfigOptionModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "claude-sonnet-4-5", h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsNull();
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option")).IsFalse();
    }

    [Test]
    public async Task TrySelectAsync_ModelsPropertyMalformed_ReturnsNull_NoThrow() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        // "models" present but shaped as a bare string, not an object — deserializing it against
        // SessionModelsInfo throws JsonException, which must be caught (not propagate).
        var sessionNewResult = JsonDocument.Parse($$"""{"sessionId":"{{FakeAcpAgent.FixedSessionId}}","models":"not-an-object"}""").RootElement;

        var result = await ConfigOptionModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "claude-sonnet-4-5", h.Logger, h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(result).IsNull();
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option")).IsFalse();
    }

    // (e) a ct canceled WHILE session/set_config_option is in flight — TrySelectAsync must let
    // OperationCanceledException propagate out, never returning null for a cancellation the way it
    // does for a resolution failure (spec-review Finding 2).
    [Test]
    public async Task TrySelectAsync_CanceledWhileSetConfigOptionInFlight_PropagatesOperationCanceled() {
        await using var h = new Harness();
        h.Fake.HoldSetConfigOptionResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.StartFakeAgentLoop();

        var sessionNewResult = FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "composer-2.5[fast=true]", AvailableModels);

        using var innerCts = new CancellationTokenSource();

        var selectTask = ConfigOptionModelSelector.Instance
            .TrySelectAsync(h.Conn, FakeAcpAgent.FixedSessionId, sessionNewResult, requestedModel: "claude-sonnet-4-5", h.Logger, innerCts.Token);

        // Wait for the RPC to actually be in flight (recorded by the fake) before cancelling.
        var deadline = DateTime.UtcNow + HangGuard;
        while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option") && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option")).IsTrue();

        await innerCts.CancelAsync();

        await Assert.That(async () => await selectTask.WaitAsync(HangGuard)).Throws<OperationCanceledException>();

        // Release the fake's held response so the harness can tear down cleanly.
        h.Fake.HoldSetConfigOptionResponse.TrySetResult();
    }
}
