using System.IO.Pipelines;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// The Kiro reviewer's launch shape, asserted on the ARGV and environment the process would receive
/// — not on a round's outcome. A round that completes proves nothing about whether a tool was
/// trusted if the model never called it.
/// </summary>
public class KiroReviewerLaunchTests {
    const string InstalledVersion = "2.16.0";

    static string StateDir() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-kiro-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static DaemonConfig EnabledConfig(string stateDir) {
        var config = new DaemonConfig {
            KiroUnattendedReviewerEnabled = true,
            StateDir = stateDir,
            Name = "test-daemon",
            DaemonEpoch = "epoch-1"
        };

        // Seeded exactly as enabling the reviewer does in production: without it every launch is
        // refused over an upgrade that never happened.
        new KiroReviewerVersionStore(AcpHostedAgentRuntimeFactory.ReviewerStateDir(config))
            .Affirm(InstalledVersion);

        return config;
    }

    static RuntimeStartContext Ctx(bool isReviewFlow, string[]? mcpAllowlist = null) => new RuntimeStartContext(
        AgentId: "agent-1", Vendor: "kiro", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "",
        Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: isReviewFlow ? "http://kcap.test" : null,
        DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap")
        with { McpAllowlist = mcpAllowlist };

    static System.Diagnostics.ProcessStartInfo Psi(
            bool isReviewFlow, DaemonConfig config, string[]? mcpAllowlist = null) =>
        AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Kiro, config, Ctx(isReviewFlow, mcpAllowlist),
            resolveGeminiVersion: _ => InstalledVersion);

    static string TrustValue(System.Diagnostics.ProcessStartInfo psi) {
        var i = psi.ArgumentList.IndexOf("--trust-tools");
        return i >= 0 && i + 1 < psi.ArgumentList.Count ? psi.ArgumentList[i + 1] : "";
    }

    [Test]
    public async Task AReviewLaunch_TrustsReadAndThink_AndNeverWriteOrShell() {
        var value = TrustValue(Psi(isReviewFlow: true, EnabledConfig(StateDir())));

        await Assert.That(value.Split(',')).Contains("fs_read");
        await Assert.That(value.Split(',')).Contains("thinking");
        await Assert.That(value).DoesNotContain("fs_write");
        await Assert.That(value).DoesNotContain("execute_bash");
    }

    /// <summary>
    /// The case a FIXED trust list fails. Asserting the ARGV rather than a round outcome is what makes
    /// this non-vacuous: a reviewer that never calls an allowlisted tool completes identically whether
    /// or not the tool was trusted.
    /// </summary>
    [Test]
    public async Task AReviewLaunchWithAnAllowlist_TrustsThatServersTools() {
        var psi   = Psi(isReviewFlow: true, EnabledConfig(StateDir()), mcpAllowlist: ["kcap-review"]);
        var value = TrustValue(psi);

        // Every trusted namespaced entry must name a server this launch actually injects, and every
        // injected non-result server's tools must appear.
        await Assert.That(value).Contains("kcap-review");

        foreach (var tool in KcapMcpRegistry.ReviewFlowUnattendedSafeTools["kcap-review"])
            await Assert.That(value).Contains($"/{tool}");
    }

    /// <summary>The control: an interactive launch gets neither the trust argv nor an isolated home,
    /// because a hosted Kiro the user drives should behave exactly as their own session does.</summary>
    [Test]
    public async Task AnInteractiveLaunch_HasNoTrustArgvAndNoIsolatedHome() {
        var psi = Psi(isReviewFlow: false, EnabledConfig(StateDir()));

        await Assert.That(psi.ArgumentList.Contains("--trust-tools")).IsFalse();
        await Assert.That(psi.Environment.ContainsKey("KIRO_HOME")).IsFalse();
    }

    [Test]
    public async Task AReviewLaunch_SetsAnEmptyOwnerOnlyKiroHome() {
        var psi = Psi(isReviewFlow: true, EnabledConfig(StateDir()));

        await Assert.That(psi.Environment.ContainsKey("KIRO_HOME")).IsTrue();

        var home = psi.Environment["KIRO_HOME"]!;
        await Assert.That(Directory.Exists(home)).IsTrue();
        await Assert.That(Directory.GetFileSystemEntries(home).Length).IsEqualTo(0);

        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(home)).IsEqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task ADisabledDaemon_RefusesAReviewLaunch() {
        var config = new DaemonConfig { StateDir = StateDir(), Name = "test-daemon" };

        await Assert.That(() => Psi(isReviewFlow: true, config))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("kiro_unattended_reviewer_disabled");
    }

    /// <summary>
    /// The gate must fire on an upgrade. Asserted with the operator flag ON, so this cannot pass for
    /// the wrong reason (a disabled daemon refuses everything).
    /// </summary>
    [Test]
    public async Task AnUpgradedKiro_RefusesAReviewLaunchUntilAffirmed() {
        var config = EnabledConfig(StateDir());

        await Assert.That(() => AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Kiro, config, Ctx(isReviewFlow: true),
                resolveGeminiVersion: _ => "2.17.0"))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("kiro_reviewer_version_unaffirmed");
    }

    /// <summary>
    /// A peer that is ALIVE and never answers. This is the production shape — measured, an
    /// unauthenticated kiro-cli prints "Opening browser..." and stays alive indefinitely rather than
    /// failing — and it is the one case the two terminating fixtures (an unresolvable binary, a peer
    /// that exits before initialize) structurally cannot produce.
    /// </summary>
    [Test]
    public async Task AnAliveButSilentPeer_HitsTheDeadlineAndIsReaped() {
        var config = EnabledConfig(StateDir());
        config.KiroReviewerLaunchTimeoutSeconds = 1;

        // Streams that never yield a frame: the child is up, the pipe is open, nothing arrives.
        var silentIn  = new Pipe();
        var silentOut = new Pipe();
        var process   = new AliveSilentProcess();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Kiro,
            config: config,
            loggerFactory: NullLoggerFactory.Instance,
            connection: new SilentServerConnection(),
            connectionSource: _ => (silentIn.Writer.AsStream(), silentOut.Reader.AsStream(), process),
            resolveVendorVersion: _ => InstalledVersion);

        var ex = await Assert.That(async () =>
            await factory.StartAsync(Ctx(isReviewFlow: true), CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(ex!.Message).StartsWith("kiro_reviewer_launch_timeout");

        // Reaped, not merely abandoned — otherwise the child and its transcript-bearing home outlive
        // the round the server has already given up on.
        await Assert.That(process.Terminated).IsTrue();
    }

    sealed class SilentServerConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance) { }

    sealed class AliveSilentProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int  Pid       => 31337;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }
        public bool Terminated { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            Terminated = true;
            HasExited  = true;
            ExitCode   = 137;
            _exited.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
