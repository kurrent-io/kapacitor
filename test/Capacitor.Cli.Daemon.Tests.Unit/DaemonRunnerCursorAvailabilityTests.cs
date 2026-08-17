using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <c>DaemonRunner.RunAsync</c> silently omits an unavailable vendor from
/// <c>DaemonConfig.SupportedVendors</c> (correct — the launch dialog just won't offer it), but gave
/// operators no clue WHY when that vendor is Cursor. <see cref="DaemonRunner.ShouldWarnCursorUnavailable"/>
/// is the pure predicate extracted from that startup seam so it's testable without spinning up the
/// full DI host <c>RunAsync</c> builds — this only proves the predicate; the actual
/// <c>LogCursorUnavailable</c> Warning call at the <c>RunAsync</c> call site is not independently
/// unit-tested (would require a full host boot).
/// </summary>
public class DaemonRunnerCursorAvailabilityTests {
    /// <summary>Minimal <see cref="IHostedAgentRuntimeFactory"/> stand-in — only <see cref="Vendor"/>/<see cref="IsAvailable"/>/<see cref="SupportsUnattended"/>/<see cref="ReviewerModelResolver"/> matter here.</summary>
    sealed class FakeRuntimeFactory(
            string vendor, bool isAvailable, bool supportsUnattended = false,
            bool supportsBorrowedReviewFlow = false,
            IReviewerModelResolver? reviewerModelResolver = null,
            string? borrowedReviewContainment = null) : IHostedAgentRuntimeFactory {
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended { get; } = supportsUnattended;
        public bool   SupportsBorrowedReviewFlow { get; } = supportsBorrowedReviewFlow;
        public string? BorrowedReviewContainment  { get; } = borrowedReviewContainment;
        public IReviewerModelResolver? ReviewerModelResolver { get; } = reviewerModelResolver;

        public bool IsAvailable() => isAvailable;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    /// <summary>Fake reviewer-model resolver — only <see cref="Vendor"/>/<see cref="PolicyVersion"/>
    /// matter for the capability-advertisement tests (advertisement is vendor-neutral: it reads
    /// whether a resolver exists + its policy version, never a model string).</summary>
    sealed class FakeReviewerModelResolver(string vendor, string policyVersion) : IReviewerModelResolver {
        public string Vendor        { get; } = vendor;
        public string PolicyVersion { get; } = policyVersion;

        public ReviewerModelResolution Resolve(string requestedModel) =>
            new(ReviewerModelDisposition.Unavailable);
    }

    [Test]
    public async Task ShouldWarnCursorUnavailable_CursorFactoryUnavailable_ReturnsTrue() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true),
            new FakeRuntimeFactory("cursor", isAvailable: false),
        ];

        await Assert.That(DaemonRunner.ShouldWarnCursorUnavailable(factories)).IsTrue();
    }

    [Test]
    public async Task ShouldWarnCursorUnavailable_CursorFactoryAvailable_ReturnsFalse() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true),
            new FakeRuntimeFactory("cursor", isAvailable: true),
        ];

        await Assert.That(DaemonRunner.ShouldWarnCursorUnavailable(factories)).IsFalse();
    }

    [Test]
    public async Task ShouldWarnCursorUnavailable_NoCursorFactoryRegistered_ReturnsFalse() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true),
            new FakeRuntimeFactory("codex", isAvailable: false),
        ];

        await Assert.That(DaemonRunner.ShouldWarnCursorUnavailable(factories)).IsFalse();
    }

    // === Reviewer vendor override: UnattendedVendors computation ===

    [Test]
    public async Task ComputeUnattendedVendors_IncludesAvailableCopilot_WhenUnattendedEnabled() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("copilot", isAvailable: true, supportsUnattended: true),
        ];

        await Assert.That(DaemonRunner.ComputeUnattendedVendors(factories, new DaemonConfig()))
            .IsEquivalentTo(["claude", "copilot"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ComputeUnattendedVendors_ExcludesAvailableCursor_WhenItDoesNotSupportUnattended() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("cursor", isAvailable: true, supportsUnattended: false),
        ];

        await Assert.That(DaemonRunner.ComputeUnattendedVendors(factories, new DaemonConfig())).IsEquivalentTo(["claude", "codex"]);
    }

    [Test]
    public async Task ComputeUnattendedVendors_ExcludesUnavailableVendorEvenIfItSupportsUnattended() {
        // Claude installed but unavailable (binary not found) must not be advertised, regardless
        // of what SupportsUnattended says — installation is still a prerequisite.
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: false, supportsUnattended: true),
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
        ];

        await Assert.That(DaemonRunner.ComputeUnattendedVendors(factories, new DaemonConfig())).IsEquivalentTo(["codex"]);
    }

    [Test]
    public async Task ComputeUnattendedVendors_OrdersAlphabetically() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true),
        ];

        await Assert.That(DaemonRunner.ComputeUnattendedVendors(factories, new DaemonConfig())).IsEquivalentTo(["claude", "codex"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ComputeUnattendedVendors_NoFactoriesReturnsEmptyArray() {
        await Assert.That(DaemonRunner.ComputeUnattendedVendors([], new DaemonConfig())).IsEmpty();
    }

    // === Withheld-vendor startup diagnostic ===
    //
    // A vendor this daemon could host unattended but is refusing used to leave advertisement in silence:
    // the refusal text existed, but only the launch path threw it, and advertisement is exactly what stops
    // the launch from being attempted. Classification carries the reason out so startup can log it.

    /// <summary>Counts <see cref="IHostedAgentRuntimeFactory.DescribeUnattendedSupport"/> calls, because
    /// answering it spawns the vendor binary for the gated reviewers.</summary>
    sealed class WithholdingRuntimeFactory(string vendor, string? withheldReason, bool isAvailable = true)
            : IHostedAgentRuntimeFactory {
        public int Describes { get; private set; }

        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended => DescribeUnattendedSupport().Supported;

        public UnattendedSupport DescribeUnattendedSupport() {
            Describes++;

            return new(withheldReason is null, withheldReason);
        }

        public bool IsAvailable() => isAvailable;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("not exercised by this test");
    }

    [Test]
    public async Task ClassifyUnattendedVendors_CarriesTheReasonAVendorIsWithheld() {
        IHostedAgentRuntimeFactory[] factories = [
            new WithholdingRuntimeFactory("gemini", "gemini_unattended_reviewer_disabled: …"),
            new WithholdingRuntimeFactory("cursor", withheldReason: null),
        ];

        var statuses = DaemonRunner.ClassifyUnattendedVendors(factories);

        var gemini = statuses.Single(s => s.Vendor == "gemini");
        await Assert.That(gemini.Advertised).IsFalse();
        await Assert.That(gemini.WithheldReason).IsEqualTo("gemini_unattended_reviewer_disabled: …");

        var cursor = statuses.Single(s => s.Vendor == "cursor");
        await Assert.That(cursor.Advertised).IsTrue();
        await Assert.That(cursor.WithheldReason).IsNull()
            .Because("an advertised vendor is withholding nothing, so it must not produce a warning");
    }

    /// <summary>A vendor that simply does not offer unattended hosting is not "withheld" — nothing an
    /// operator can act on, and warning about it would train them to ignore the ones that are.</summary>
    [Test]
    public async Task ClassifyUnattendedVendors_DoesNotReportADeclinedVendorAsWithheld() {
        // The default interface implementation's shape: Supported=false, no reason.
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("cursor", isAvailable: true, supportsUnattended: false),
        ];

        var statuses = DaemonRunner.ClassifyUnattendedVendors(factories);

        await Assert.That(statuses.Single().Advertised).IsFalse();
        await Assert.That(statuses.Single().WithheldReason).IsNull();
    }

    /// <summary>An UNINSTALLED vendor is not classified at all: it is already covered by its own
    /// not-found diagnostic, and asking would spawn a binary that is not there.</summary>
    [Test]
    public async Task ClassifyUnattendedVendors_SkipsAnUnavailableVendorEntirely() {
        var absent = new WithholdingRuntimeFactory("gemini", "would explain a refusal", isAvailable: false);

        var statuses = DaemonRunner.ClassifyUnattendedVendors([absent]);

        await Assert.That(statuses).IsEmpty();
        await Assert.That(absent.Describes).IsEqualTo(0);
    }

    /// <summary>
    /// One classification, one probe per vendor.
    ///
    /// <para>Startup needs the advertised list, the capability list AND the diagnostic. Deriving all three
    /// from one classification is what keeps a gated reviewer's `--version` spawn — bounded at 10s per
    /// attempt — from happening three times on every boot.</para>
    /// </summary>
    [Test]
    public async Task ClassifyUnattendedVendors_AsksEachFactoryExactlyOnce() {
        var gemini = new WithholdingRuntimeFactory("gemini", "refused");
        var cursor = new WithholdingRuntimeFactory("cursor", withheldReason: null);

        var statuses = DaemonRunner.ClassifyUnattendedVendors([gemini, cursor]);

        // Derive both downstream products from that one classification, as RunAsync does.
        var advertised = DaemonRunner.AdvertisedUnattendedVendors(statuses);
        _ = DaemonRunner.ComputeUnattendedVendorCapabilities(
            [gemini, cursor], new DaemonConfig(), advertised);

        await Assert.That(advertised).IsEquivalentTo(["cursor"]);
        await Assert.That(gemini.Describes).IsEqualTo(1);
        await Assert.That(cursor.Describes).IsEqualTo(1);
    }

    /// <summary>
    /// The subset filter, exercised on its own inputs rather than against the composition it is part of.
    ///
    /// <para>Review's point, and it was right: comparing <c>AdvertisedUnattendedVendors(Classify(x))</c>
    /// to <c>ComputeUnattendedVendors(x)</c> is now comparing a composition to its own definition, so it
    /// could never fail for either function independently. Feeding statuses directly tests the one thing
    /// this helper does — keep the advertised, drop the rest, preserve order.</para>
    /// </summary>
    [Test]
    public async Task AdvertisedUnattendedVendors_KeepsOnlyTheAdvertisedOnesInOrder() {
        DaemonRunner.UnattendedVendorStatus[] statuses = [
            new("claude", Advertised: true,  WithheldReason: null),
            new("gemini", Advertised: false, WithheldReason: "refused"),
            // Advertised is the only thing consulted — a reason on an advertised row (which the
            // classifier never produces) must not remove it, or the filter is reading the wrong field.
            new("kiro",   Advertised: true,  WithheldReason: "ignored"),
            new("zed",    Advertised: false, WithheldReason: null),
        ];

        await Assert.That(DaemonRunner.AdvertisedUnattendedVendors(statuses))
            .IsEquivalentTo(["claude", "kiro"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ComputeUnattendedVendorCapabilities_AdvertisesClaudePolicyFacts() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            factories, new DaemonConfig { ClaudePath = "/definitely/missing/claude" });

        await Assert.That(capabilities).Count().IsEqualTo(2);
        var claude = capabilities.Single(c => c.Vendor == "claude");
        await Assert.That(claude.CliVersion).IsNull();
        await Assert.That(claude.LauncherPolicyVersion)
            .IsEqualTo(DaemonRunner.ClaudeLauncherPolicyVersion);
        await Assert.That(claude.BorrowedReviewSupported).IsFalse();
    }

    /// <summary>Launch-posture support is advertised per vendor: Codex accepts a caller-selected
    /// sandbox/approval block, every other vendor does not. The server refuses posture selection
    /// unless this is explicitly true, so a wrong default here would either silently drop a
    /// selection or offer one no launcher honours.</summary>
    [Test]
    public async Task ComputeUnattendedVendorCapabilities_AdvertisesLaunchPostureForCodexOnly() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true),
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(factories, new DaemonConfig());

        await Assert.That(capabilities.Single(c => c.Vendor == "codex").SupportsLaunchPosture).IsTrue();
        await Assert.That(capabilities.Single(c => c.Vendor == "claude").SupportsLaunchPosture).IsFalse();
    }

    // === Trust-by-default borrowed-review advertisement ===
    // (docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md)
    //
    // These replace an earlier test that pinned the opposite rule ("a Cursor build that doesn't
    // match the validated-build record advertises BorrowedReviewSupported=false"). That gate is the
    // bug: Cursor auto-updates, the daemon silently withdrew borrowed capability, and the server
    // then resolved workspace_mode=fallback — reviewing a stale committed base with nobody told.

    /// <summary>THE regression test for this bug. The configured Cursor path deliberately does not
    /// resolve to the validated build (it does not resolve to any binary at all, which is a strictly
    /// harder non-match than an updated build), and Cursor is STILL advertised borrowed-capable with
    /// its containment token. Note the same inputs also drive the null-CliVersion path: an
    /// unidentifiable build is still a trusted build.</summary>
    [Test]
    public async Task ComputeUnattendedVendorCapabilities_CursorBuildNotMatchingValidationRecord_StillAdvertisesBorrowed() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("cursor", isAvailable: true, supportsUnattended: true,
                supportsBorrowedReviewFlow: true, borrowedReviewContainment: "independent-snapshot"),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            factories, new DaemonConfig { CursorPath = "/definitely/missing/cursor-agent" });

        await Assert.That(capabilities).Count().IsEqualTo(1);
        await Assert.That(capabilities[0].Vendor).IsEqualTo("cursor");
        await Assert.That(capabilities[0].LauncherPolicyVersion)
            .IsEqualTo(DaemonRunner.CursorLauncherPolicyVersion);
        await Assert.That(capabilities[0].BorrowedReviewSupported).IsTrue();
        await Assert.That(capabilities[0].BorrowedReviewContainment).IsEqualTo("independent-snapshot");
        // An unprobeable version does not cost the vendor its capability.
        await Assert.That(capabilities[0].CliVersion).IsNull();
    }

    /// <summary>The gate that REMAINS: borrowed support is exactly the factory's own
    /// <see cref="IHostedAgentRuntimeFactory.SupportsBorrowedReviewFlow"/>, so a factory that does
    /// not support borrowed review is still never advertised (and carries no containment token).
    /// Cursor is used deliberately — the removed special case was Cursor-only, so proving the
    /// remaining gate on Cursor proves it was not replaced by a blanket "always true".</summary>
    [Test]
    public async Task ComputeUnattendedVendorCapabilities_FactoryWithoutBorrowedSupport_IsNotAdvertised() {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("cursor", isAvailable: true, supportsUnattended: true,
                supportsBorrowedReviewFlow: false, borrowedReviewContainment: "independent-snapshot"),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            factories, new DaemonConfig { CursorPath = "/definitely/missing/cursor-agent" });

        await Assert.That(capabilities[0].BorrowedReviewSupported).IsFalse();
        await Assert.That(capabilities[0].BorrowedReviewContainment).IsNull();
    }

    /// <summary>The advertisement is a pure function of the factory flag for EVERY vendor token —
    /// no residual per-vendor arm survives for Cursor or anyone else.</summary>
    [Test]
    [Arguments("cursor", true)]
    [Arguments("cursor", false)]
    [Arguments("copilot", true)]
    [Arguments("copilot", false)]
    [Arguments("claude", true)]
    [Arguments("newvendor", true)]
    public async Task ComputeUnattendedVendorCapabilities_BorrowedAdvertisementMirrorsTheFactoryFlag(
            string vendor, bool supportsBorrowed) {
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory(vendor, isAvailable: true, supportsUnattended: true,
                supportsBorrowedReviewFlow: supportsBorrowed,
                borrowedReviewContainment: "independent-snapshot"),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(factories, new DaemonConfig());

        await Assert.That(capabilities[0].BorrowedReviewSupported).IsEqualTo(supportsBorrowed);
    }

    // === Platform coverage ===
    //
    // Dropping the gate also dropped the macOS/arm64 preconditions that lived inside the
    // validated-build record, so borrowed review is now advertised on EVERY OS and architecture
    // where the vendor CLI is installed. The design accepts that expansion: the snapshot path
    // (WorktreeManager.CreateBorrowedSnapshotAsync / SyncFromSourceAsync) is platform-neutral git +
    // managed file operations with explicit Windows branches, and neither the ACP factory nor the
    // Cursor descriptor carries any other OS/arch gate.
    //
    // Coverage matrix for that claim:
    //   • linux-x64    — asserted, executed on CI's ubuntu-latest runner.
    //   • windows-x64  — asserted, executed on CI's windows-latest runner.
    //   • macos-arm64  — asserted, executed on maintainer machines (the suite's development host).
    //   • macos-x64    — INTENTIONALLY UNTESTED. It has no runner in this repo's CI matrix, and
    //     adding one is not worthwhile here: the unit suite has a known cluster of macOS-local
    //     temp-path/parallel-load flakes that would make a macOS CI leg permanently red, and macOS
    //     is already exercised on arm64 by maintainers. The residual risk is bounded by
    //     AdvertisementPathHasNoBuildIdentityGate below, which proves — from any host — that no
    //     platform-conditional build-identity check remains in either the advertisement or the
    //     launch path, so no macos-x64-specific behavior can differ from macos-arm64 here.

    /// <summary>Asserts the advertisement on whichever platform is executing, so the CI matrix
    /// (linux-x64, windows-x64) plus maintainer runs (macos-arm64) each independently prove it. The
    /// host is named in the assertion message so a platform-specific failure is legible.</summary>
    [Test]
    public async Task ComputeUnattendedVendorCapabilities_AdvertisesCursorBorrowed_OnTheExecutingPlatform() {
        var host = $"{RuntimeInformation.RuntimeIdentifier} ({RuntimeInformation.OSDescription.Trim()})";
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("cursor", isAvailable: true, supportsUnattended: true,
                supportsBorrowedReviewFlow: true, borrowedReviewContainment: "independent-snapshot"),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            factories, new DaemonConfig { CursorPath = "/definitely/missing/cursor-agent" });

        await Assert.That(capabilities[0].BorrowedReviewSupported)
            .IsTrue().Because($"borrowed review must be advertised on {host}");
        await Assert.That(capabilities[0].BorrowedReviewContainment)
            .IsEqualTo("independent-snapshot").Because($"containment must be advertised on {host}");
    }

    /// <summary>
    /// A source-level guard, in the shape of <c>ReviewerModelVendorNeutralityGuardTests</c>: neither
    /// the advertisement path (<c>DaemonRunner.cs</c>) nor the launch path
    /// (<c>AcpHostedAgentRuntimeFactory.cs</c>) may consult the validated-build record. This is what
    /// makes the platform matrix above sound — a re-introduced gate would fail here from ANY host,
    /// including for the macos-x64 leg no runner executes, whereas a behavioral assertion can only
    /// speak for the platform it runs on. The record's own file and its tests are of course allowed
    /// to name it.
    /// </summary>
    [Test]
    public async Task AdvertisementAndLaunchPaths_HaveNoBuildIdentityGate() {
        var daemonSrc = Path.Combine(RepoRoot(), "src", "Capacitor.Cli.Daemon");
        var guarded = new[] {
            Path.Combine(daemonSrc, "DaemonRunner.cs"),
            Path.Combine(daemonSrc, "Services", "AcpHostedAgentRuntimeFactory.cs"),
        };

        var violations = new List<string>();
        foreach (var file in guarded) {
            var lines = await File.ReadAllLinesAsync(file);
            for (var i = 0; i < lines.Length; i++) {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (lines[i].Contains("TryMatchValidatedBuild", StringComparison.Ordinal) ||
                    lines[i].Contains("CursorBorrowedReviewArtifact", StringComparison.Ordinal))
                    violations.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        await Assert.That(violations).IsEmpty();
    }

    /// <summary>Self-test for the guard above — it must actually detect a re-introduced gate rather
    /// than pass because the file paths are wrong or the scan is inert.</summary>
    [Test]
    public async Task AdvertisementGuard_DetectsAReintroducedBuildIdentityGate() {
        using var tmp = new TempDir();
        var dir = tmp.Path;

        var file = Path.Combine(dir, "DaemonRunner.cs");
        await File.WriteAllLinesAsync(file, [
            "// a comment naming TryMatchValidatedBuild must NOT count",
            "var artifact = CursorBorrowedReviewValidation.TryMatchValidatedBuild(cliPath);",
        ]);

        var lines = await File.ReadAllLinesAsync(file);
        var hits = lines
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Count(l => l.Contains("TryMatchValidatedBuild", StringComparison.Ordinal));

        await Assert.That(hits).IsEqualTo(1);
    }

    /// <summary>Walks up from this file's compile-time path to the repo root, so the guard is
    /// independent of the runner's working directory.</summary>
    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"Could not locate repo root walking up from {here}");
    }

    // === Startup identity logging ===

    /// <summary>Records rendered log entries so the startup identity line can be asserted without
    /// booting the DI host <c>RunAsync</c> builds.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    [Test]
    public async Task LogUnattendedVendorIdentities_EmitsOneInformationLinePerVendor_NamingTheProbedVersion() {
        var logger = new CaptureLogger();

        DaemonRunner.LogUnattendedVendorIdentities(logger, [
            new("claude", "2.1.0", DaemonRunner.ClaudeLauncherPolicyVersion, false),
            new("cursor", "2026.07.23-e383d2b", DaemonRunner.CursorLauncherPolicyVersion, true, "independent-snapshot"),
        ]);

        var info = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        await Assert.That(info).Count().IsEqualTo(2);
        await Assert.That(info).Contains(e => e.Message.Contains("claude") && e.Message.Contains("2.1.0"));
        await Assert.That(info).Contains(e => e.Message.Contains("cursor") && e.Message.Contains("2026.07.23-e383d2b"));
    }

    /// <summary>A probe that produced nothing usable renders the literal <c>unknown</c> — never a
    /// blank, never an omitted line — and the vendor keeps its borrowed capability, since an
    /// unidentifiable build is still a trusted build.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task LogUnattendedVendorIdentities_NullOrBlankProbe_RendersUnknown_AndDoesNotAffectCapability(
            string? probed) {
        var logger = new CaptureLogger();
        var capability = new UnattendedVendorCapability(
            "cursor", probed, DaemonRunner.CursorLauncherPolicyVersion, true, "independent-snapshot");

        DaemonRunner.LogUnattendedVendorIdentities(logger, [capability]);

        var line = logger.Entries.Single(e => e.Level == LogLevel.Information).Message;
        await Assert.That(line).Contains("unknown");
        await Assert.That(capability.BorrowedReviewSupported).IsTrue();
        await Assert.That(capability.BorrowedReviewContainment).IsEqualTo("independent-snapshot");
    }

    /// <summary>The wording must mark the value as a daemon-startup observation, so nobody reads it
    /// as the build a later reviewer actually ran — an update after startup makes it stale, which is
    /// precisely the situation that caused this bug.</summary>
    [Test]
    public async Task LogUnattendedVendorIdentities_WordsTheVersionAsAStartupObservation_NotALaunchFact() {
        var logger = new CaptureLogger();

        DaemonRunner.LogUnattendedVendorIdentities(logger, [
            new("cursor", "2026.07.23-e383d2b", DaemonRunner.CursorLauncherPolicyVersion, true, "independent-snapshot"),
        ]);

        var line = logger.Entries.Single().Message;
        await Assert.That(line).Contains("daemon startup");
        await Assert.That(line).Contains("stale");
    }

    /// <summary>NEGATIVE test, and it is load-bearing: the startup line reports the version and
    /// nothing else. Computing whether the installed build agrees with the validated-build record
    /// would be automated version-drift detection — the thing this design deliberately does not do —
    /// and would hand the demoted record a production caller. A test that demanded such an
    /// assertion would drag the rejected behavior straight back in.</summary>
    [Test]
    public async Task LogUnattendedVendorIdentities_DoesNotCompareAgainstTheValidatedBuildRecord() {
        var logger = new CaptureLogger();

        DaemonRunner.LogUnattendedVendorIdentities(logger, [
            new("cursor", "2026.07.23-e383d2b", DaemonRunner.CursorLauncherPolicyVersion, true, "independent-snapshot"),
        ]);

        var line = logger.Entries.Single().Message;
        await Assert.That(line).DoesNotContain("2026.07.20-8cc9c0b");
        foreach (var token in new[] { "certif", "validated", "mismatch", "drift", "uncertified" })
            await Assert.That(line.Contains(token, StringComparison.OrdinalIgnoreCase))
                .IsFalse().Because($"the startup identity line must not evaluate build agreement ('{token}')");
    }

    [Test]
    public async Task CliVersionAllowed_EvaluatesConfiguredRange() {
        await Assert.That(DaemonRunner.CliVersionAllowed("v1.2.3", ">=1.2.0 <2.0.0")).IsTrue();
        await Assert.That(DaemonRunner.CliVersionAllowed("2.0.0", ">=1.2.0 <2.0.0")).IsFalse();
        await Assert.That(DaemonRunner.CliVersionAllowed(null, ">=1.2.0")).IsFalse();
        await Assert.That(DaemonRunner.CliVersionAllowed("1.2.3", "")).IsFalse();
    }

    // === Reviewer model resolution: capability advertisement ===

    [Test]
    public async Task ComputeUnattendedVendorCapabilities_WithResolver_AdvertisesReviewerModelResolution() {
        // Installed + unattended-certified + HAS a resolver ⇒ the vendor advertises reviewer-model
        // resolution and its policy version.
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("claude", isAvailable: true, supportsUnattended: true,
                reviewerModelResolver: new FakeReviewerModelResolver("claude", "claude-reviewer-model-v1")),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            factories, new DaemonConfig { ClaudePath = "/definitely/missing/claude" });

        var claude = capabilities.Single(c => c.Vendor == "claude");
        await Assert.That(claude.SupportsReviewerModelResolution).IsTrue();
        await Assert.That(claude.ReviewerModelPolicyVersion).IsEqualTo("claude-reviewer-model-v1");
    }

    [Test]
    public async Task ComputeUnattendedVendorCapabilities_WithoutResolver_DoesNotAdvertiseReviewerModelResolution() {
        // Installed + unattended-certified but NO resolver ⇒ the vendor keeps its vendor-only
        // unattended support but must advertise false/null (never widened to "supported").
        IHostedAgentRuntimeFactory[] factories = [
            new FakeRuntimeFactory("codex", isAvailable: true, supportsUnattended: true),
        ];

        var capabilities = DaemonRunner.ComputeUnattendedVendorCapabilities(
            factories, new DaemonConfig { CodexPath = "/definitely/missing/codex" });

        var codex = capabilities.Single(c => c.Vendor == "codex");
        await Assert.That(codex.SupportsReviewerModelResolution).IsFalse();
        await Assert.That(codex.ReviewerModelPolicyVersion).IsNull();
    }
}
