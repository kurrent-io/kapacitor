// test/Capacitor.Cli.Tests.Unit/Daemon/ReviewerModelResolutionTests.cs
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Runtime-owned reviewer MODEL override resolution. Covers the cross-vendor coordinator's disposition
/// matrix with fake vendors (selected-acceptance-wins / vendor_mismatch with the ordinal-first
/// diagnostic vendor / unavailable / invalid) and the two currently-authoritative real launcher
/// policies (Claude, Codex) — in particular the equivalence-key stability the server validates by
/// EQUALITY (a bare alias and the dated concrete id it resolves to must share one anchor).
/// </summary>
public class ReviewerModelResolutionTests {
    /// <summary>Minimal per-vendor resolver: accepts a fixed set of model ids (returning a
    /// vendor-scoped anchor), unavailable otherwise. Never itself returns vendor_mismatch — that is the
    /// coordinator's cross-vendor conclusion.</summary>
    sealed class FakeResolver(string vendor, params string[] recognized) : IReviewerModelResolver {
        public string Vendor        => vendor;
        public string PolicyVersion => $"{vendor}-fake-v1";

        public ReviewerModelResolution Resolve(string requestedModel) =>
            recognized.Contains(requestedModel, StringComparer.Ordinal)
                ? new(ReviewerModelDisposition.Accept,
                    CanonicalRequestedModel: requestedModel,
                    LaunchModel: requestedModel,
                    EquivalenceKey: $"{vendor}/{requestedModel}")
                : new(ReviewerModelDisposition.Unavailable);
    }

    // === Coordinator disposition matrix (fake vendors) ===

    [Test]
    public async Task Coordinator_SelectedAcceptanceWins_ForSharedModel() {
        // Both vendors recognize "shared"; the SELECTED vendor's resolution must win — not the
        // ordinal-first one.
        IReviewerModelResolver[] resolvers = [
            new FakeResolver("aardvark", "shared"),
            new FakeResolver("zebra", "shared"),
        ];

        var r = ReviewerModelResolvers.Resolve("zebra", "shared", resolvers);

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Accept);
        await Assert.That(r.EquivalenceKey).IsEqualTo("zebra/shared");
    }

    [Test]
    public async Task Coordinator_SelectedRejects_OneOtherRecognizes_ReturnsVendorMismatch() {
        IReviewerModelResolver[] resolvers = [
            new FakeResolver("aardvark", "amodel"),
            new FakeResolver("zebra", "zmodel"),
        ];

        var r = ReviewerModelResolvers.Resolve("zebra", "amodel", resolvers);

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.VendorMismatch);
        await Assert.That(r.DiagnosticCode).IsEqualTo("aardvark");
    }

    [Test]
    public async Task Coordinator_SelectedRejects_ManyOthersRecognize_NamesOrdinalFirstVendor() {
        // "shared" is recognized by both "aardvark" and "beaver"; the selected "zebra" rejects it.
        // The mismatch must name the ORDINAL-first other recognizer ("aardvark"), regardless of the
        // list order the resolvers were supplied in.
        IReviewerModelResolver[] resolvers = [
            new FakeResolver("beaver", "shared"),
            new FakeResolver("zebra", "zmodel"),
            new FakeResolver("aardvark", "shared"),
        ];

        var r = ReviewerModelResolvers.Resolve("zebra", "shared", resolvers);

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.VendorMismatch);
        await Assert.That(r.DiagnosticCode).IsEqualTo("aardvark");
    }

    [Test]
    public async Task Coordinator_NoRecognizer_ReturnsUnavailable() {
        IReviewerModelResolver[] resolvers = [
            new FakeResolver("aardvark", "amodel"),
            new FakeResolver("zebra", "zmodel"),
        ];

        var r = ReviewerModelResolvers.Resolve("zebra", "nobody-knows-this", resolvers);

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Unavailable);
        await Assert.That(r.DiagnosticCode).IsNull();
    }

    [Test]
    public async Task Coordinator_InvalidInput_ReturnsInvalid() {
        IReviewerModelResolver[] resolvers = [new FakeResolver("zebra", "zmodel")];

        var r = ReviewerModelResolvers.Resolve("zebra", "   ", resolvers);

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Invalid);
    }

    [Test]
    public async Task Coordinator_InvalidInput_ShortCircuitsBeforeVendorScan() {
        // A model that a DIFFERENT vendor would "recognize" as a string must still be invalid when it
        // fails syntax hygiene — malformed input is vendor-independent and never becomes a mismatch.
        IReviewerModelResolver[] resolvers = [
            new FakeResolver("aardvark", "bad\nmodel"),
            new FakeResolver("zebra", "zmodel"),
        ];

        var r = ReviewerModelResolvers.Resolve("zebra", "bad\nmodel", resolvers);

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Invalid);
    }

    [Test]
    public async Task Coordinator_UnknownSelectedVendor_ReturnsUnavailable() {
        IReviewerModelResolver[] resolvers = [new FakeResolver("zebra", "zmodel")];

        var r = ReviewerModelResolvers.Resolve("ghost", "zmodel", resolvers);

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Unavailable);
    }

    // === Real Claude launcher policy ===

    [Test]
    public async Task Claude_AliasAndDatedId_ShareOneStableEquivalenceKey() {
        var resolver = ClaudeReviewerModelResolver.Instance;

        var alias = resolver.Resolve("sonnet");
        var dated = resolver.Resolve("claude-sonnet-4-5-20250929");

        await Assert.That(alias.Disposition).IsEqualTo(ReviewerModelDisposition.Accept);
        await Assert.That(dated.Disposition).IsEqualTo(ReviewerModelDisposition.Accept);
        // THE CRUX: a bare alias and the dated concrete id it resolves to canonicalize to ONE anchor,
        // so the server can validate the daemon's later concrete-model report by equality.
        await Assert.That(alias.EquivalenceKey).IsEqualTo(dated.EquivalenceKey);
        // Anchor requirement: every accept returns a non-null equivalence key.
        await Assert.That(alias.EquivalenceKey).IsNotNull();
    }

    [Test]
    public async Task Claude_OldAndNewDatedFormats_ShareTheSameFamilyKey() {
        var resolver = ClaudeReviewerModelResolver.Instance;

        var newFmt = resolver.Resolve("claude-sonnet-4-5-20250929");
        var oldFmt = resolver.Resolve("claude-3-5-sonnet-20241022");

        await Assert.That(newFmt.EquivalenceKey).IsEqualTo(oldFmt.EquivalenceKey);
    }

    [Test]
    public async Task Claude_DistinctFamilies_HaveDistinctKeys() {
        var resolver = ClaudeReviewerModelResolver.Instance;

        var opus   = resolver.Resolve("opus");
        var sonnet = resolver.Resolve("sonnet");

        await Assert.That(opus.EquivalenceKey).IsNotEqualTo(sonnet.EquivalenceKey);
    }

    [Test]
    public async Task Claude_LaunchModel_IsPassedThroughVerbatim() {
        var r = ClaudeReviewerModelResolver.Instance.Resolve("claude-opus-4-1-20250805");

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Accept);
        await Assert.That(r.LaunchModel).IsEqualTo("claude-opus-4-1-20250805");
    }

    [Test]
    public async Task Claude_UnknownModel_IsUnavailable() {
        // A well-formed but non-Claude slug is unavailable (not invalid) — the coordinator can then
        // still surface it as another vendor's model.
        var r = ClaudeReviewerModelResolver.Instance.Resolve("gpt-5-codex");

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Unavailable);
    }

    [Test]
    public async Task Claude_MalformedInput_IsInvalid() {
        var r = ClaudeReviewerModelResolver.Instance.Resolve("has a space");

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Invalid);
    }

    // === Real Codex launcher policy ===

    [Test]
    public async Task Codex_KnownSlug_AcceptedWithSlugLevelKey() {
        var r = CodexReviewerModelResolver.Instance.Resolve("gpt-5-codex");

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Accept);
        await Assert.That(r.EquivalenceKey).IsEqualTo("codex/gpt-5-codex");
        await Assert.That(r.LaunchModel).IsEqualTo("gpt-5-codex");
    }

    [Test]
    public async Task Codex_ReasoningSeriesSlug_Accepted() {
        var r = CodexReviewerModelResolver.Instance.Resolve("o3");

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Accept);
        await Assert.That(r.EquivalenceKey).IsNotNull();
    }

    [Test]
    public async Task Codex_ClaudeAlias_IsUnavailable() {
        var r = CodexReviewerModelResolver.Instance.Resolve("sonnet");

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.Unavailable);
    }

    // === Real resolvers wired through the coordinator ===

    [Test]
    public async Task RealResolvers_ClaudeModelSelectedForCodex_ReportsClaudeMismatch() {
        IReviewerModelResolver[] resolvers = [
            ClaudeReviewerModelResolver.Instance,
            CodexReviewerModelResolver.Instance,
        ];

        // The user picked codex as the reviewer vendor but asked for a Claude model.
        var r = ReviewerModelResolvers.Resolve("codex", "sonnet", resolvers);

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.VendorMismatch);
        await Assert.That(r.DiagnosticCode).IsEqualTo("claude");
    }

    [Test]
    public async Task RealResolvers_CodexModelSelectedForClaude_AcceptedForCodexAsMismatch() {
        IReviewerModelResolver[] resolvers = [
            ClaudeReviewerModelResolver.Instance,
            CodexReviewerModelResolver.Instance,
        ];

        var r = ReviewerModelResolvers.Resolve("claude", "gpt-5-codex", resolvers);

        await Assert.That(r.Disposition).IsEqualTo(ReviewerModelDisposition.VendorMismatch);
        await Assert.That(r.DiagnosticCode).IsEqualTo("codex");
    }
}
