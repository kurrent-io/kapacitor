using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>
/// Pi's hosted-launch invariants, asserted on the LAUNCH ARTIFACT — the
/// <see cref="System.Diagnostics.ProcessStartInfo"/> <see cref="PiRpcHostedAgentRuntimeFactory.BuildPsi"/>
/// actually produces — plus the factory's refusal ladder and DI registration.
/// </summary>
public class PiHostedLaunchTests {
    static RuntimeStartContext Ctx(
            string? model = null, string? prompt = "", bool isReview = false, bool isReviewFlow = false,
            WorkLocation work = WorkLocation.OwnedWorktree, string? serverUrl = "http://kcap.test",
            string daemonId = "", string daemonEpoch = "") => new(
        AgentId: "agent-1", Vendor: "pi", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: prompt,
        Model: model, Effort: null, Tools: null,
        IsReview: isReview, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: serverUrl, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        Work: work, DaemonId: daemonId, DaemonEpoch: daemonEpoch);

    // ── BuildPsi: argv ──

    [Test]
    public async Task BuildPsi_NoModel_ArgvIsExactlyModeRpc() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx());

        await Assert.That(string.Join(" ", psi.ArgumentList)).IsEqualTo("--mode rpc");
    }

    [Test]
    public async Task BuildPsi_WithCallerModel_ArgvAppendsModelFlag() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(model: "claude-opus"));

        await Assert.That(string.Join(" ", psi.ArgumentList)).IsEqualTo("--mode rpc --model claude-opus");
    }

    [Test]
    public async Task BuildPsi_WithConfigDefaultModel_ArgvAppendsModelFlag() {
        var config = new DaemonConfig { PiModel = "config-default" };
        var psi    = PiRpcHostedAgentRuntimeFactory.BuildPsi(config, Ctx());

        await Assert.That(string.Join(" ", psi.ArgumentList)).IsEqualTo("--mode rpc --model config-default");
    }

    [Test]
    public async Task BuildPsi_UsesConfiguredPiPath() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig { PiPath = "/opt/pi/bin/pi" }, Ctx());

        await Assert.That(psi.FileName).IsEqualTo("/opt/pi/bin/pi");
    }

    // ── BuildPsi: cwd ──

    [Test]
    public async Task BuildPsi_WorkingDirectoryIsExactlyTheWorktreePath() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx());

        await Assert.That(psi.WorkingDirectory).IsEqualTo("/abs/wt");
    }

    // ── BuildPsi: env — all five vars, exact ──

    [Test]
    public async Task BuildPsi_EnvCarriesTheDualCaptureGate() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx());

        await Assert.That(psi.Environment[PiLaunchEnvironment.PureVariable]).IsEqualTo("1");
    }

    [Test]
    public async Task BuildPsi_EnvCarriesAllFiveVarsExactly() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(
            new DaemonConfig(),
            Ctx(serverUrl: "http://kcap.test", daemonId: "daemon-1", daemonEpoch: "epoch-1"));

        await Assert.That(psi.Environment["KCAP_PI_PURE"]).IsEqualTo("1");
        await Assert.That(psi.Environment["KCAP_URL"]).IsEqualTo("http://kcap.test");
        await Assert.That(psi.Environment["KCAP_AGENT_ID"]).IsEqualTo("agent-1");
        await Assert.That(psi.Environment["KCAP_DAEMON_ID"]).IsEqualTo("daemon-1");
        await Assert.That(psi.Environment["KCAP_DAEMON_EPOCH"]).IsEqualTo("epoch-1");
    }

    [Test]
    public async Task BuildPsi_OmitsDaemonIdAndEpoch_WhenContextCarriesNone() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(daemonId: "", daemonEpoch: ""));

        await Assert.That(psi.Environment.ContainsKey("KCAP_DAEMON_ID")).IsFalse();
        await Assert.That(psi.Environment.ContainsKey("KCAP_DAEMON_EPOCH")).IsFalse();
    }

    [Test]
    public async Task BuildPsi_OmitsServerUrl_WhenContextCarriesNone() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(serverUrl: null));

        await Assert.That(psi.Environment.ContainsKey("KCAP_URL")).IsFalse();
    }

    [Test]
    public async Task BuildPsi_RedirectsAllThreeStandardStreams() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx());

        await Assert.That(psi.RedirectStandardInput).IsTrue();
        await Assert.That(psi.RedirectStandardOutput).IsTrue();
        await Assert.That(psi.RedirectStandardError).IsTrue();
    }

    // ── ResolveModel matrix ──

    [Test]
    public async Task ResolveModel_CtxModelWins_OverConfigDefault() {
        var config = new DaemonConfig { PiModel = "config-default" };

        await Assert.That(PiRpcHostedAgentRuntimeFactory.ResolveModel(config, Ctx(model: "caller-choice")))
            .IsEqualTo("caller-choice");
    }

    [Test]
    [Arguments("default")]
    [Arguments("DEFAULT")]
    [Arguments("Default")]
    public async Task ResolveModel_DefaultSentinel_FallsThroughToConfig(string sentinel) {
        var config = new DaemonConfig { PiModel = "config-default" };

        await Assert.That(PiRpcHostedAgentRuntimeFactory.ResolveModel(config, Ctx(model: sentinel)))
            .IsEqualTo("config-default");
    }

    [Test]
    public async Task ResolveModel_BothNull_ResolvesNull() {
        await Assert.That(PiRpcHostedAgentRuntimeFactory.ResolveModel(new DaemonConfig(), Ctx(model: null))).IsNull();
    }

    [Test]
    public async Task ResolveModel_EmptyCtxModel_FallsThroughToConfig() {
        var config = new DaemonConfig { PiModel = "config-default" };

        await Assert.That(PiRpcHostedAgentRuntimeFactory.ResolveModel(config, Ctx(model: ""))).IsEqualTo("config-default");
    }

    // ── IsAvailable seam ──

    [Test]
    public async Task IsAvailable_ProbesConfiguredPiPath_ThroughTheSeam() {
        string? probed = null;
        var factory = new PiRpcHostedAgentRuntimeFactory(
            new DaemonConfig { PiPath = "/opt/pi/bin/pi" }, NullLoggerFactory.Instance,
            binaryExists: p => { probed = p; return true; });

        var result = factory.IsAvailable();

        await Assert.That(result).IsTrue();
        await Assert.That(probed).IsEqualTo("/opt/pi/bin/pi");
    }

    [Test]
    public async Task IsAvailable_FalseWhenSeamReportsMissing() {
        var factory = new PiRpcHostedAgentRuntimeFactory(
            new DaemonConfig(), NullLoggerFactory.Instance, binaryExists: _ => false);

        await Assert.That(factory.IsAvailable()).IsFalse();
    }

    // ── Vendor / unattended-support surface ──

    [Test]
    public async Task Vendor_IsPi() {
        var factory = new PiRpcHostedAgentRuntimeFactory(new DaemonConfig(), NullLoggerFactory.Instance);

        await Assert.That(factory.Vendor).IsEqualTo("pi");
    }

    [Test]
    public async Task SupportsUnattended_IsFalse_InPr1() {
        var factory = new PiRpcHostedAgentRuntimeFactory(new DaemonConfig(), NullLoggerFactory.Instance);

        await Assert.That(factory.SupportsUnattended).IsFalse();
    }

    [Test]
    public async Task DescribeUnattendedSupport_WithheldReasonIsNull_PiNeverClaimedUnattendedSupport() {
        // WithheldReason is reserved for a vendor this daemon's OWN config is refusing to offer —
        // Pi simply doesn't support it yet, so the default IHostedAgentRuntimeFactory implementation
        // (no override here) must report null, not a reason. A prior revision's override reported a
        // non-null reason, which made every daemon with pi installed log a false "restart to enable"
        // operator instruction at boot.
        IHostedAgentRuntimeFactory factory = new PiRpcHostedAgentRuntimeFactory(new DaemonConfig(), NullLoggerFactory.Instance);

        // A default interface member is only reachable through the interface type — there is no
        // override on the concrete class to call directly, which is the whole point of this fix.
        var support = factory.DescribeUnattendedSupport();

        await Assert.That(support.Supported).IsFalse();
        await Assert.That(support.WithheldReason).IsNull();
    }

    [Test]
    public async Task SupportsModelSelection_IsTrue() {
        var factory = new PiRpcHostedAgentRuntimeFactory(new DaemonConfig(), NullLoggerFactory.Instance);

        await Assert.That(factory.SupportsModelSelection).IsTrue();
    }

    // ── StartAsync refusals — none of these may reach a spawn ──

    [Test]
    public async Task StartAsync_RefusesAPrReview() {
        var factory = new PiRpcHostedAgentRuntimeFactory(
            new DaemonConfig(), NullLoggerFactory.Instance,
            processSource: (_, _) => throw new InvalidOperationException("must not spawn"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(Ctx(isReview: true), CancellationToken.None));

        await Assert.That(ex!.Message).Contains("pi_pr_review_unsupported");
    }

    [Test]
    public async Task StartAsync_RefusesAReviewFlowLaunch() {
        var factory = new PiRpcHostedAgentRuntimeFactory(
            new DaemonConfig(), NullLoggerFactory.Instance,
            processSource: (_, _) => throw new InvalidOperationException("must not spawn"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(Ctx(isReviewFlow: true), CancellationToken.None));

        await Assert.That(ex!.Message).Contains("pi_reviewer_not_implemented");
    }

    [Test]
    public async Task StartAsync_RefusesABorrowedWorkspace() {
        var factory = new PiRpcHostedAgentRuntimeFactory(
            new DaemonConfig(), NullLoggerFactory.Instance,
            processSource: (_, _) => throw new InvalidOperationException("must not spawn"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(Ctx(work: WorkLocation.BorrowedCwd), CancellationToken.None));

        await Assert.That(ex!.Message).Contains("pi_requires_owned_worktree");
    }

    // ── StartAsync: the happy path against a fake process ──

    /// <summary>Minimal <see cref="IPiRpcProcess"/> stand-in that answers <c>get_state</c> immediately
    /// with a fixed session id, so <c>WaitForSessionReadyAsync</c> resolves without a real child.</summary>
    sealed class FakeProcess : IPiRpcProcess {
        readonly System.Threading.Channels.Channel<string> _lines =
            System.Threading.Channels.Channel.CreateUnbounded<string>();

        public int     Pid         => 4242;
        public bool    HasExited   => false;
        public int?    ExitCode    => null;
        public string? Diagnostics => null;

        public List<string> Written { get; } = [];

        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct) => _lines.Reader.ReadAllAsync(ct);

        public Task WriteLineAsync(string json, CancellationToken ct) {
            Written.Add(json);

            // Answer the handshake's get_state with a fixed session id the instant it's sent, so the
            // ready barrier resolves without any real child process.
            if (json.Contains("\"get_state\"") && json.Contains(PiRpcHostedAgentRuntime.InitStateCommandId)) {
                _lines.Writer.TryWrite(
                    "{\"type\":\"response\",\"id\":\"" + PiRpcHostedAgentRuntime.InitStateCommandId
                  + "\",\"success\":true,\"data\":{\"sessionId\":\"sess-1\"}}");
            }

            return Task.CompletedTask;
        }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan? timeout = null)   => Task.CompletedTask;
        public ValueTask DisposeAsync()                        => ValueTask.CompletedTask;
    }

    [Test]
    public async Task StartAsync_HappyPath_ReturnsARuntimeBoundToTheResolvedSession() {
        var fake = new FakeProcess();
        var factory = new PiRpcHostedAgentRuntimeFactory(
            new DaemonConfig(), NullLoggerFactory.Instance,
            processSource: (_, _) => Task.FromResult<IPiRpcProcess>(fake));

        var start = await factory.StartAsync(Ctx(prompt: "hello"), CancellationToken.None);

        try {
            await Assert.That(start.Transcript!.AcpSessionId).IsEqualTo("sess-1");
            await Assert.That(start.McpConfigPath).IsNull();
            await Assert.That(fake.Written.Any(w => w.Contains("\"prompt\""))).IsTrue();
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task StartAsync_DoesNotSendAnEmptyPrompt() {
        var fake = new FakeProcess();
        var factory = new PiRpcHostedAgentRuntimeFactory(
            new DaemonConfig(), NullLoggerFactory.Instance,
            processSource: (_, _) => Task.FromResult<IPiRpcProcess>(fake));

        var start = await factory.StartAsync(Ctx(prompt: ""), CancellationToken.None);

        try {
            await Assert.That(fake.Written.Any(w => w.Contains("\"prompt\""))).IsFalse();
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>A process whose handshake never answers — <c>WaitForSessionReadyAsync</c> must fault
    /// (bounded by the runtime's own internal ready deadline) and the factory must dispose the runtime
    /// rather than leak it.</summary>
    sealed class SilentProcess : IPiRpcProcess {
        public int     Pid         => 9999;
        public bool    HasExited   => false;
        public int?    ExitCode    => null;
        public bool    Disposed    { get; private set; }
        public string? Diagnostics { get; set; }

        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct) =>
            System.Threading.Channels.Channel.CreateUnbounded<string>().Reader.ReadAllAsync(ct);

        public Task WriteLineAsync(string json, CancellationToken ct) => Task.CompletedTask;
        public Task WaitForExitAsync(TimeSpan? timeout = null)        => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan? timeout = null)          => Task.CompletedTask;

        public ValueTask DisposeAsync() {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task StartAsync_ASilentChild_FaultsAndDisposesRatherThanHanging() {
        var silent  = new SilentProcess();
        var factory = new PiRpcHostedAgentRuntimeFactory(
            new DaemonConfig(), NullLoggerFactory.Instance,
            processSource: (_, _) => Task.FromResult<IPiRpcProcess>(silent),
            // A short test-only deadline (see the factory ctor's readyDeadline param) instead of
            // burning the real 30s DefaultReadyDeadline this launch would otherwise wait out.
            readyDeadline: TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<Exception>(() => factory.StartAsync(Ctx(), CancellationToken.None));

        await Assert.That(silent.Disposed).IsTrue();
    }

    /// <summary>Security regression: the child's captured stderr must stay DAEMON-LOCAL. Stderr can
    /// carry prompt fragments, paths, or auth detail — the same reason
    /// <c>PiRpcProcess.DrainStderrAsync</c> never logs it either — and the thrown exception's
    /// <c>Message</c> is exactly what <c>AgentOrchestrator</c> forwards, verbatim, off-host to the
    /// server via <c>LaunchFailedAsync</c>. So the message must carry only a generic, non-sensitive
    /// indicator, while the raw stderr text lands in the daemon's own log instead.</summary>
    [Test]
    public async Task StartAsync_ASilentChildWithDiagnostics_KeepsStderrOutOfTheThrownMessage() {
        var silent        = new SilentProcess { Diagnostics = "authentication required: run `pi login`" };
        var loggerFactory = new CaptureLoggerFactory();
        var factory = new PiRpcHostedAgentRuntimeFactory(
            new DaemonConfig(), loggerFactory,
            processSource: (_, _) => Task.FromResult<IPiRpcProcess>(silent),
            readyDeadline: TimeSpan.FromMilliseconds(500));

        var ex = await Assert.ThrowsAsync<Exception>(() => factory.StartAsync(Ctx(), CancellationToken.None));

        await Assert.That(ex!.Message).DoesNotContain("authentication required");
        await Assert.That(ex.Message).Contains("stderr captured in daemon log");

        // The raw text must still be RECOVERABLE — just from the access-controlled local sink, not
        // from the exception that left the daemon.
        await Assert.That(loggerFactory.Logger.Entries
            .Any(e => e.Level == LogLevel.Warning && e.Message.Contains("authentication required")))
            .IsTrue();
    }

    /// <summary>Records every log call across every category (one instance shared by every
    /// <c>CreateLogger&lt;T&gt;()</c> call) — mirrors <c>AcpHostedAgentRuntimeFactoryTests.CaptureLogger</c>'s
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

    // ── DI registration: after DaemonRunner's registration shape, "pi" resolves ──

    /// <summary>
    /// Mirrors the exact registration DaemonRunner uses (one <c>IHostedAgentRuntimeFactory</c>
    /// singleton per vendor projected into the vendor-keyed dictionary
    /// <c>AgentOrchestrator</c> resolves by), without booting the full daemon host — proving the
    /// wiring rather than re-asserting DaemonRunner's own source text.
    /// </summary>
    [Test]
    public async Task DaemonRunnerRegistrationShape_ResolvesPiFromTheVendorDictionary() {
        var services = new ServiceCollection();
        services.AddSingleton(new DaemonConfig());
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new PiRpcHostedAgentRuntimeFactory(
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
            )
        );

        services.AddSingleton<IReadOnlyDictionary<string, IHostedAgentRuntimeFactory>>(sp =>
            sp.GetServices<IHostedAgentRuntimeFactory>().ToDictionary(f => f.Vendor)
        );

        await using var provider = services.BuildServiceProvider();

        var factories = provider.GetRequiredService<IReadOnlyDictionary<string, IHostedAgentRuntimeFactory>>();

        await Assert.That(factories.ContainsKey("pi")).IsTrue();
        await Assert.That(factories["pi"].Vendor).IsEqualTo("pi");
    }
}
