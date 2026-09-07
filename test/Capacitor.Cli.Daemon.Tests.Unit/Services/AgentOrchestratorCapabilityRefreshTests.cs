using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="AgentOrchestrator.RefreshAdvertisedCapabilities"/>: the one path through which a
/// running daemon re-advertises its vendor CLI versions, shared by the binary watcher and the
/// certification rejection. Re-probes the real stub binary rather than faking the probe, so what is
/// pinned is the advertisement the server would see.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorCapabilityRefreshTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string MissingCli = "/definitely/missing/claude";

    string StubClaude(string version) {
        var path = Tmp.CreateFile("claude", $"#!/bin/sh\necho '{version} (Claude Code)'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    static UnattendedVendorCapability Advertised(string? version) =>
        new("claude", version, DaemonRunner.ClaudeLauncherPolicyVersion, false);

    static (AgentOrchestrator Orchestrator, CaptureServerConnection Server, DaemonConfig Config) Build(
            string cliPath, IReadOnlyList<UnattendedVendorCapability>? advertised) {
        var server   = new CaptureServerConnection();
        var launcher = new SpyHostedAgentLauncher("claude", cliPath) { SupportsUnattended = true };
        DaemonConfig? captured = null;
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher> { ["claude"] = launcher },
            configure: config => {
                config.UnattendedVendors             = ["claude"];
                config.UnattendedVendorCapabilities  = advertised;
                captured = config;
            });
        return (orch, server, captured!);
    }

    static string? ClaudeVersion(DaemonConfig config) =>
        config.UnattendedVendorCapabilities!.Single(c => c.Vendor == "claude").CliVersion;

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_changed_binary_is_re_advertised_with_its_installed_version() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");
        var (orch, server, config) = Build(StubClaude("2.1.263"), [Advertised("2.1.259")]);
        await using var _ = orch;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(ClaudeVersion(config)).IsEqualTo("2.1.263");
        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unchanged_advertisement_is_not_re_registered() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");
        var (orch, server, _) = Build(StubClaude("2.1.263"), advertised: null);
        await using var __ = orch;

        orch.RefreshAdvertisedCapabilities("first");
        await orch.CapabilityRefreshForTest;
        orch.RefreshAdvertisedCapabilities("second");
        await orch.CapabilityRefreshForTest;

        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_reprobe_keeps_the_advertised_version() {
        var (orch, _, config) = Build(MissingCli, [Advertised("2.1.259")]);
        await using var __ = orch;

        orch.RefreshAdvertisedCapabilities("test");
        await orch.CapabilityRefreshForTest;

        await Assert.That(ClaudeVersion(config)).IsEqualTo("2.1.259");
    }

    // A certification rejection means the server's copy disagrees with the installed binary, so
    // that path republishes even when the local advertisement already reads the same.
    [Test]
    public async Task A_republish_request_re_registers_an_unchanged_advertisement() {
        var (orch, server, _) = Build(MissingCli, advertised: null);
        await using var __ = orch;

        orch.RefreshAdvertisedCapabilities("first");
        await orch.CapabilityRefreshForTest;
        orch.RefreshAdvertisedCapabilities("rejection", republishUnchanged: true);
        await orch.CapabilityRefreshForTest;

        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(2);
    }

    // The refresh is single-flighted, so a rejection's republish request that lands while a
    // watcher-triggered pass is running is folded into that pass or its rerun. Folding must not
    // drop the republish, or the retry the rejection promised meets the same stale server copy.
    [Test]
    public async Task A_republish_request_folded_into_a_running_pass_still_re_registers() {
        var (orch, server, _) = Build(MissingCli, advertised: null);
        await using var __ = orch;

        orch.RefreshAdvertisedCapabilities("first");
        await orch.CapabilityRefreshForTest;

        orch.RefreshAdvertisedCapabilities("watcher");
        orch.RefreshAdvertisedCapabilities("rejection", republishUnchanged: true);
        await orch.CapabilityRefreshForTest;

        await Assert.That(server.RegisterDaemonCalls).IsEqualTo(2);
    }
}
