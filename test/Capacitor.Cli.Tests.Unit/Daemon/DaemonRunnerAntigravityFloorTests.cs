using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// The Antigravity minimum-version floor at the surface that decides ADVERTISEMENT. A vendor the
/// daemon does not advertise is refused server-side before a launch is attempted, so this is where
/// the floor has to hold — and the reason has to travel with the refusal, because an unadvertised
/// vendor otherwise vanishes in silence.
/// </summary>
public class DaemonRunnerAntigravityFloorTests {
    sealed class FakeFactory(string vendor, bool advertised) : IHostedAgentRuntimeFactory {
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended { get; } = advertised;

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    static DaemonConfig Config(string minimum = "1.1.10") => new() {
        AntigravityPath                      = "agy",
        AntigravityUnattendedReviewerEnabled = true,
        AntigravityMinimumCliVersion         = minimum,
        Name                                 = "test-daemon"
    };

    static IReadOnlyList<DaemonRunner.UnattendedVendorStatus> Floor(
            DaemonConfig config, string? probed, params DaemonRunner.UnattendedVendorStatus[] statuses) =>
        DaemonRunner.ApplyAntigravityVersionFloor(statuses, config, _ => probed);

    static DaemonRunner.UnattendedVendorStatus Advertised(string vendor) => new(vendor, true, null);

    [Test]
    public async Task AFloorMeetingBuild_StaysAdvertised() {
        var result = Floor(Config(), "1.1.10", Advertised("antigravity"), Advertised("claude"));

        await Assert.That(result.Single(s => s.Vendor == "antigravity").Advertised).IsTrue();
        await Assert.That(result.Single(s => s.Vendor == "antigravity").WithheldReason).IsNull();
    }

    [Test]
    public async Task ABelowFloorBuild_IsWithheldWithAReasonThatNamesBothVersions() {
        var result = Floor(Config(), "1.1.8", Advertised("antigravity"));
        var status = result.Single();

        await Assert.That(status.Advertised).IsFalse();
        await Assert.That(status.WithheldReason!).StartsWith("antigravity_reviewer_version_below_minimum");
        await Assert.That(status.WithheldReason!).Contains("1.1.8");
        await Assert.That(status.WithheldReason!).Contains("1.1.10");
    }

    /// <summary>A probe that could not identify the build refuses under its OWN arm — the operator's
    /// next action is to fix the binary, not to upgrade it.</summary>
    [Test]
    public async Task AnUnidentifiableBuild_IsWithheldAsUnresolved() {
        var status = Floor(Config(), null, Advertised("antigravity")).Single();

        await Assert.That(status.Advertised).IsFalse();
        await Assert.That(status.WithheldReason!).StartsWith("antigravity_reviewer_version_unresolved");
    }

    /// <summary>The floor only ever NARROWS. A vendor the factory's own ladder already withheld keeps
    /// that refusal — overwriting it would replace the operator's actual problem with a version one.</summary>
    [Test]
    public async Task AVendorTheFactoryAlreadyWithheld_KeepsItsOwnReason() {
        DaemonRunner.UnattendedVendorStatus withheld = new("antigravity", false, "antigravity_reviewer_binary_missing: …");

        var status = Floor(Config(), "0.0.1", withheld).Single();

        await Assert.That(status.Advertised).IsFalse();
        await Assert.That(status.WithheldReason!).StartsWith("antigravity_reviewer_binary_missing");
    }

    /// <summary>Never probes, and never touches, a classification this vendor is not part of.</summary>
    [Test]
    public async Task AClassificationWithoutAntigravity_IsReturnedUntouchedAndUnprobed() {
        var probed = 0;

        var result = DaemonRunner.ApplyAntigravityVersionFloor(
            [Advertised("claude"), Advertised("kiro")],
            Config(),
            _ => { probed++; return "0.0.1"; });

        await Assert.That(result.All(s => s.Advertised)).IsTrue();
        await Assert.That(probed).IsEqualTo(0);
    }

    /// <summary>The binary is probed ONCE — the decision and its explanation both need the version,
    /// and resolving it per consumer spawns the vendor binary twice to produce one refusal.</summary>
    [Test]
    public async Task TheVendorBinaryIsProbedExactlyOnce() {
        var probed = 0;

        DaemonRunner.ApplyAntigravityVersionFloor(
            [Advertised("antigravity")], Config(), _ => { probed++; return "1.1.8"; });

        await Assert.That(probed).IsEqualTo(1);
    }

    /// <summary>The floor is probed against the path the DAEMON would launch, not whatever `agy`
    /// happens to resolve to first.</summary>
    [Test]
    public async Task TheProbeReadsTheConfiguredBinaryPath() {
        string? seen  = null;
        var config    = Config();
        config.AntigravityPath = "/opt/agy/bin/agy";

        DaemonRunner.ApplyAntigravityVersionFloor(
            [Advertised("antigravity")], config, path => { seen = path; return "1.1.10"; });

        await Assert.That(seen).IsEqualTo("/opt/agy/bin/agy");
    }

    /// <summary>The whole point of the owner's decision: a newer build is not refused. An
    /// affirmation-style exact compare reintroduced at this seam would fail here.</summary>
    [Test]
    public async Task ANewerBuildThanTheFloor_StaysAdvertised() {
        var status = Floor(Config(), "2.5.0", Advertised("antigravity")).Single();

        await Assert.That(status.Advertised).IsTrue();
    }

    /// <summary>
    /// The floor is part of the vendor-list computation itself, not only of the startup path that
    /// happens to call it. <c>ComputeUnattendedVendors</c> is what the orchestrator's post-rejection
    /// capability refresh re-derives from, so a floor applied only at boot would silently re-advertise
    /// a refused build the first time a launch was rejected.
    /// </summary>
    [Test]
    public async Task TheComputedVendorListAppliesTheFloorToo() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary below is a POSIX shell script.");

        var stub = await StubAgyAsync("0.9.0");

        try {
            var config = Config();
            config.AntigravityPath = stub;

            var vendors = DaemonRunner.ComputeUnattendedVendors(
                [new FakeFactory("antigravity", advertised: true), new FakeFactory("claude", advertised: true)],
                config);

            await Assert.That(vendors).IsEquivalentTo(new[] { "claude" });
        } finally {
            File.Delete(stub);
        }
    }

    static async Task<string> StubAgyAsync(string version) {
        var stub = Path.Combine(Path.GetTempPath(), "kcap-agy-stub-" + Guid.NewGuid().ToString("N"));

        await File.WriteAllTextAsync(stub, $"#!/bin/sh\necho 'Antigravity CLI {version}'\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return stub;
    }

    /// <summary>
    /// The advertised capability carries the build this daemon would actually launch. Without an
    /// explicit arm, antigravity falls through to the generic one — which advertises no CLI version at
    /// all, even though there is a configured path right there to probe. The policy version is
    /// identical either way, so a real probe of a real executable is the only assertion that can tell
    /// the two apart.
    /// </summary>
    [Test]
    public async Task TheAdvertisedCapabilityCarriesTheProbedCliVersion() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary below is a POSIX shell script.");

        var stub = await StubAgyAsync("1.1.10");

        try {
            var config = Config();
            config.AntigravityPath = stub;

            var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
                [new FakeFactory("antigravity", advertised: true)], config, advertised: ["antigravity"]);

            var antigravity = capabilities.Single();

            await Assert.That(antigravity.CliVersion).IsEqualTo("1.1.10");
            await Assert.That(antigravity.LauncherPolicyVersion)
                .IsEqualTo(DaemonRunner.AntigravityLauncherPolicyVersion);
        } finally {
            File.Delete(stub);
        }
    }
}
