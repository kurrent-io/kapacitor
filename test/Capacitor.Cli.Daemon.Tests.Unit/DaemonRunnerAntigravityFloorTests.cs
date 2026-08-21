using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// The Antigravity minimum-version gate as seen from the DAEMON's vendor-list computation. The
/// minimum itself lives in the factory's one gate ladder (asserted arm-by-arm in
/// <c>AntigravityReviewerLaunchTests</c>); what these pin is that the list this daemon advertises
/// derives from that ladder rather than from a second, separately-maintained narrowing pass.
/// </summary>
public class DaemonRunnerAntigravityFloorTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    sealed class FakeFactory(string vendor, bool advertised) : IHostedAgentRuntimeFactory {
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended { get; } = advertised;

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    /// <param name="minimum">Recorded exactly as enabling the reviewer does in production. Null
    /// records NOTHING — the gate is a daemon-owned record, not configuration, so "unset" is an absent
    /// file rather than a value.</param>
    DaemonConfig Config(string? minimum = "1.1.10") {
        var config = new DaemonConfig {
            AntigravityPath                      = "agy",
            AntigravityUnattendedReviewerEnabled = true,
            Store                                = Daemons.Store,
            Name                                 = "test-daemon"
        };

        // Without a record every arm below refuses as version_no_minimum instead of on the comparison
        // under test.
        if (minimum is not null)
            AntigravityHostedAgentRuntimeFactory.VersionStoreFor(config).Affirm(minimum);

        return config;
    }

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
    [UnsupportedOSPlatform("windows")]
    public async Task TheComputedVendorListAppliesTheFloorThroughTheFactory() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary below is a POSIX shell script.");

        using var stubDir = StubAgy("0.9.0", out var stub);

        var config = Config();
        config.AntigravityPath = stub;

        var vendors = DaemonRunner.ComputeUnattendedVendors(
            [new AntigravityHostedAgentRuntimeFactory(config, NullLoggerFactory.Instance),
             new FakeFactory("claude", advertised: true)],
            config);

        await Assert.That(vendors).IsEquivalentTo(new[] { "claude" });
    }

    /// <summary>The positive twin of the test above — without it, an antigravity that never advertised
    /// for ANY reason (a broken stub, a path that does not resolve) would satisfy the exclusion.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task TheComputedVendorListKeepsAFloorMeetingBuild() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary below is a POSIX shell script.");

        using var stubDir = StubAgy("1.1.10", out var stub);

        var config = Config();
        config.AntigravityPath = stub;

        var vendors = DaemonRunner.ComputeUnattendedVendors(
            [new AntigravityHostedAgentRuntimeFactory(config, NullLoggerFactory.Instance)], config);

        await Assert.That(vendors).IsEquivalentTo(new[] { "antigravity" });
    }

    /// <summary>
    /// The daemon SEEDS the record and the factory READS it — two paths that must name the same file,
    /// with nothing in the type system making them. Get the directory shape or the vendor key wrong
    /// and every launch refuses as <c>version_no_minimum</c> forever, while both halves look correct
    /// in isolation.
    ///
    /// <para>Seeds through <c>DaemonRunner.SeedReviewerAffirmation</c> at <c>config.StateDirectory</c>
    /// — the one directory <c>RunAsync</c> hands it — and then asserts through advertisement rather
    /// than by reading the file back, which would only re-derive the writer's own answer.</para>
    /// </summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task TheDaemonsSeededRecordIsTheOneTheFactoryReads() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary below is a POSIX shell script.");

        using var stubDir = StubAgy("1.1.10", out var stub);

        var config = Config(minimum: null);
        config.AntigravityPath = stub;

        // The per-name state root RunAsync passes to the seeding block.
        var stateDir = config.Store.StateDirectory(config.Name);

        DaemonRunner.SeedReviewerAffirmation(
            stateDir, DaemonRunner.AntigravityVendor, enabled: true, stub);

        var vendors = DaemonRunner.ComputeUnattendedVendors(
            [new AntigravityHostedAgentRuntimeFactory(config, NullLoggerFactory.Instance)], config);

        await Assert.That(vendors).IsEquivalentTo(new[] { "antigravity" });
    }

    /// <summary>
    /// <b>The half of the hosted-launch fix that lives in the daemon rather than the factory.</b>
    /// Antigravity's floor gates hosted launches too, and those need no reviewer consent — so a
    /// consent-less daemon must still SEED a floor. Seeded from the consent event (as Kiro and Gemini
    /// are) it never would, and every hosted launch on such a daemon would refuse as
    /// <c>version_no_minimum</c>: the consent gate removed from the front of the ladder and quietly
    /// reinstated behind it.
    ///
    /// <para>Driven through <c>SeedReviewerFloors</c> — the whole seeding block, exactly as
    /// <c>RunAsync</c> invokes it — and NOT through <c>SeedVersionFloor</c> directly. Calling the
    /// unconditional helper by hand would pin only the directory shape and leave the CONDITIONALITY,
    /// which is the half this fix changed, asserted by nothing: reinstating
    /// <c>SeedReviewerAffirmation(…, config.AntigravityUnattendedReviewerEnabled, …)</c> at the call
    /// site would still pass.</para>
    ///
    /// <para>Asserted through a real hosted <c>StartAsync</c> reaching PAST the ladder rather than by
    /// reading the record back, which would only re-derive the writer's own answer. The observable is
    /// that a turn child was REQUESTED — the first thing beyond the gate — and not the launch's
    /// outcome: <c>StartAsync</c> wraps everything after the gate in its own coded
    /// <c>InvalidOperationException</c>, so a thrown sentinel is indistinguishable from a refusal,
    /// while a turn source that was never called is exactly what a refusal looks like.</para>
    /// </summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task AConsentLessDaemonSeedsTheFloorThatAdmitsAHostedLaunch() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary below is a POSIX shell script.");

        using var stubDir = StubAgy("1.1.10", out var stub);

        var config = Config(minimum: null);
        config.AntigravityPath                      = stub;
        config.AntigravityUnattendedReviewerEnabled = false;

        // The per-name state root RunAsync passes to the seeding block.
        var stateDir = config.Store.StateDirectory(config.Name);

        DaemonRunner.SeedReviewerFloors(stateDir, config);

        var spawned = false;
        var factory = new AntigravityHostedAgentRuntimeFactory(
            config, NullLoggerFactory.Instance,
            turnSource: (_, _) => {
                spawned = true;
                throw new NotSupportedException("the launch itself is not what this test is about");
            });

        try {
            await factory.StartAsync(HostedCtx(), CancellationToken.None);
        } catch (InvalidOperationException) {
            // Expected: the turn source above cannot produce a conversation.
        }

        await Assert.That(spawned).IsTrue();

        // The control: consent is still withheld, so the REVIEWER is still not advertised. Without
        // it, seeding having somehow re-enabled the reviewer would read as a pass.
        await Assert.That(factory.SupportsUnattended).IsFalse();
    }

    /// <summary>
    /// The ASYMMETRY inside the one seeding block, asserted as a difference rather than described in a
    /// comment. With consent withheld for all three vendors and all three binaries resolvable, only
    /// antigravity records a floor — because only antigravity's floor gates something (a hosted
    /// launch) that consent does not govern.
    ///
    /// <para>Both directions are load-bearing. Without the antigravity arm, seeding it from consent
    /// like its siblings passes. Without the kiro/gemini arms, dropping the consent condition
    /// altogether — "tidying" three call sites into one unconditional loop — passes too, and silently
    /// seeds an affirmation for a reviewer nobody opted into, which is the "consent that isn't
    /// consent" failure <c>ReviewerVersionStore</c> exists to avoid.</para>
    ///
    /// <para>All three paths point at REAL stubs on purpose: with a bare <c>kiro-cli</c>/<c>gemini</c>
    /// default the siblings would record nothing because their binary does not resolve, and the test
    /// would pass with the consent condition deleted.</para>
    /// </summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task OnlyAntigravitySeedsAFloorWithoutConsent() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binaries below are POSIX shell scripts.");

        using var agyDir = StubAgy("1.1.10", out var agy);
        using var kiroDir = StubAgy("2.0.0", out var kiro);
        using var geminiDir = StubAgy("3.0.0", out var gemini);

        var config = Config(minimum: null);
        config.AntigravityPath                      = agy;
        config.KiroPath                             = kiro;
        config.GeminiPath                           = gemini;
        config.AntigravityUnattendedReviewerEnabled = false;
        config.KiroUnattendedReviewerEnabled        = false;
        config.GeminiUnattendedReviewerEnabled      = false;

        // The per-name state root RunAsync passes to the seeding block.
        var stateDir = config.Store.StateDirectory(config.Name);

        DaemonRunner.SeedReviewerFloors(stateDir, config);

        await Assert.That(ReviewerVersionStore.RecordExists(stateDir, DaemonRunner.AntigravityVendor))
            .IsTrue();
        await Assert.That(ReviewerVersionStore.RecordExists(stateDir, AcpVendorDescriptors.Kiro.Vendor))
            .IsFalse();
        await Assert.That(ReviewerVersionStore.RecordExists(stateDir, AcpVendorDescriptors.Gemini.Vendor))
            .IsFalse();
    }

    /// <summary>The positive twin of the siblings' half above: with consent GIVEN they do seed, so
    /// their absence in that test is the consent condition and not a broken stub or a mis-keyed
    /// vendor token.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task ConsentingSiblingsSeedTheirOwnFloors() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binaries below are POSIX shell scripts.");

        using var agyDir = StubAgy("1.1.10", out var agy);
        using var kiroDir = StubAgy("2.0.0", out var kiro);
        using var geminiDir = StubAgy("3.0.0", out var gemini);

        var config = Config(minimum: null);
        config.AntigravityPath                 = agy;
        config.KiroPath                        = kiro;
        config.GeminiPath                      = gemini;
        config.KiroUnattendedReviewerEnabled   = true;
        config.GeminiUnattendedReviewerEnabled = true;

        var stateDir = config.Store.StateDirectory(config.Name);

        DaemonRunner.SeedReviewerFloors(stateDir, config);

        await Assert.That(new ReviewerVersionStore(stateDir, AcpVendorDescriptors.Kiro.Vendor).Affirmed)
            .IsEqualTo("2.0.0");
        await Assert.That(new ReviewerVersionStore(stateDir, AcpVendorDescriptors.Gemini.Vendor).Affirmed)
            .IsEqualTo("3.0.0");
    }

    static RuntimeStartContext HostedCtx() => new(
        AgentId: "agent-1", Vendor: "antigravity", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: Path.GetTempPath(), Branch: "b", SourceRepo: "/repo"),
        Prompt: "do the thing",
        Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: false, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: "http://kcap.test", DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        DaemonId: "daemon-1", DaemonEpoch: "epoch-1");

    [UnsupportedOSPlatform("windows")]
    static TempDir StubAgy(string version, out string stubPath) {
        var tmp = new TempDir();
        stubPath = tmp.CreateFile("agy", $"#!/bin/sh\necho 'Antigravity CLI {version}'\n");
        File.SetUnixFileMode(stubPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return tmp;
    }

    /// <summary>
    /// The advertised capability carries the build this daemon would actually launch. Without an
    /// explicit arm, antigravity falls through to the generic one — which advertises no CLI version at
    /// all, even though there is a configured path right there to probe. The policy version is
    /// identical either way, so a real probe of a real executable is the only assertion that can tell
    /// the two apart.
    /// </summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task TheAdvertisedCapabilityCarriesTheProbedCliVersion() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary below is a POSIX shell script.");

        using var stubDir = StubAgy("1.1.10", out var stub);

        var config = Config();
        config.AntigravityPath = stub;

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            [new FakeFactory("antigravity", advertised: true)], config, advertised: ["antigravity"]);

        var antigravity = capabilities.Single();

        await Assert.That(antigravity.CliVersion).IsEqualTo("1.1.10");
        await Assert.That(antigravity.LauncherPolicyVersion)
            .IsEqualTo(DaemonRunner.AntigravityLauncherPolicyVersion);
    }
}
