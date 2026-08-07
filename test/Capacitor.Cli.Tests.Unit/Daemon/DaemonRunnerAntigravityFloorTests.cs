using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// The Antigravity minimum-version floor as seen from the DAEMON's vendor-list computation. The
/// floor itself lives in the factory's one gate ladder (asserted arm-by-arm in
/// <c>AntigravityReviewerLaunchTests</c>); what these pin is that the list this daemon advertises
/// derives from that ladder rather than from a second, separately-maintained narrowing pass.
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

    /// <summary>
    /// The floor reaches the vendor-list computation THROUGH the factory's own gate ladder, which is
    /// the only place it is now written. <c>ComputeUnattendedVendors</c> is what the orchestrator's
    /// post-rejection capability refresh re-derives from, so a floor that did not travel with the
    /// factory would silently re-advertise a refused build the first time a launch was rejected.
    ///
    /// <para>Uses the REAL factory over a stub <c>agy</c>, deliberately: a fake factory would answer
    /// <c>SupportsUnattended</c> from a field and prove nothing about where the floor lives.</para>
    /// </summary>
    [Test]
    public async Task TheComputedVendorListAppliesTheFloorThroughTheFactory() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary below is a POSIX shell script.");

        var stub = await StubAgyAsync("0.9.0");

        try {
            var config = Config();
            config.AntigravityPath = stub;

            var vendors = DaemonRunner.ComputeUnattendedVendors(
                [new AntigravityHostedAgentRuntimeFactory(config, NullLoggerFactory.Instance),
                 new FakeFactory("claude", advertised: true)],
                config);

            await Assert.That(vendors).IsEquivalentTo(new[] { "claude" });
        } finally {
            File.Delete(stub);
        }
    }

    /// <summary>The positive twin of the test above — without it, an antigravity that never advertised
    /// for ANY reason (a broken stub, a path that does not resolve) would satisfy the exclusion.</summary>
    [Test]
    public async Task TheComputedVendorListKeepsAFloorMeetingBuild() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary below is a POSIX shell script.");

        var stub = await StubAgyAsync("1.1.10");

        try {
            var config = Config();
            config.AntigravityPath = stub;

            var vendors = DaemonRunner.ComputeUnattendedVendors(
                [new AntigravityHostedAgentRuntimeFactory(config, NullLoggerFactory.Instance)], config);

            await Assert.That(vendors).IsEquivalentTo(new[] { "antigravity" });
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
