using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Unit tests for <see cref="ImportCommand.ComputePerSourceFinalCounts"/>,
/// the per-source attribution helper that feeds the Done sub-grid.
///
/// The regression this pins: routed-phase Skipped outcomes (e.g. Cursor's
/// "already current" watermark) must roll up into Excluded, not Errored.
/// </summary>
public class ImportDoneBreakdownTests {
    static ImportCommand.SessionClassification Cls(
            string                              sessionId,
            string                              vendor,
            ImportCommand.ClassificationStatus  status
        ) =>
        new() {
            SessionId  = sessionId,
            FilePath   = $"/tmp/{sessionId}.jsonl",
            EncodedCwd = "-tmp",
            Meta       = new SessionMetadata(),
            Status     = status,
            Vendor     = vendor,
        };

    // --- Routed-phase vendor (Cursor) ---

    [Test]
    public async Task routed_vendor_folds_skipped_into_excluded_not_errored() {
        // 3 sessions classified as New for Cursor; server returned:
        //   Loaded=1, Skipped=1, Failed=1
        // (Skipped = "already current" — must NOT be counted as Errored.)
        var classifications = new[] {
            Cls("c1", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c2", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c3", "cursor", ImportCommand.ClassificationStatus.New),
        };

        var counts = ImportCommand.ComputePerSourceFinalCounts(
            classifications:  classifications,
            imported:         1,
            routedOutcomes:   (Loaded: 1, Skipped: 1, Failed: 1));

        await Assert.That(counts.Loaded).IsEqualTo(1);
        await Assert.That(counts.Excluded).IsEqualTo(1);    // <-- routed Skipped lands here
        await Assert.That(counts.Errored).IsEqualTo(1);     // <-- ONLY routed Failed
    }

    [Test]
    public async Task routed_vendor_includes_classify_time_excluded_plus_routed_skipped() {
        // 2 classify-time Excluded + 1 routed Skipped → total Excluded == 3.
        var classifications = new[] {
            Cls("c1", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c2", "cursor", ImportCommand.ClassificationStatus.Excluded),
            Cls("c3", "cursor", ImportCommand.ClassificationStatus.Excluded),
        };

        var counts = ImportCommand.ComputePerSourceFinalCounts(
            classifications:  classifications,
            imported:         0,
            routedOutcomes:   (Loaded: 0, Skipped: 1, Failed: 0));

        await Assert.That(counts.Excluded).IsEqualTo(3);
        await Assert.That(counts.Errored).IsEqualTo(0);
    }

    [Test]
    public async Task routed_vendor_with_all_skipped_has_zero_errored() {
        // 5 sessions, all of which the server marked as "already current".
        // Pre-fix bug: sub-grid would show Errored=5. Post-fix: Errored=0.
        var classifications = Enumerable.Range(0, 5)
            .Select(i => Cls($"c{i}", "cursor", ImportCommand.ClassificationStatus.New))
            .ToArray();

        var counts = ImportCommand.ComputePerSourceFinalCounts(
            classifications:  classifications,
            imported:         0,
            routedOutcomes:   (Loaded: 0, Skipped: 5, Failed: 0));

        await Assert.That(counts.Errored).IsEqualTo(0);
        await Assert.That(counts.Excluded).IsEqualTo(5);
        await Assert.That(counts.Loaded).IsEqualTo(0);
    }

    // --- Chain-phase vendor (Claude / Codex) ---

    [Test]
    public async Task chain_vendor_uses_imported_count_for_loaded() {
        // No routed outcomes available — falls back to the chain approximation:
        // Loaded = imported; Errored = (New+Partial) - imported.
        var classifications = new[] {
            Cls("s1", "claude", ImportCommand.ClassificationStatus.New),
            Cls("s2", "claude", ImportCommand.ClassificationStatus.New),
            Cls("s3", "claude", ImportCommand.ClassificationStatus.Partial),
            Cls("s4", "claude", ImportCommand.ClassificationStatus.AlreadyLoaded),
        };

        var counts = ImportCommand.ComputePerSourceFinalCounts(
            classifications:  classifications,
            imported:         2,
            routedOutcomes:   null);

        await Assert.That(counts.Loaded).IsEqualTo(2);
        await Assert.That(counts.AlreadyLoaded).IsEqualTo(1);
        // (New=2 + Partial=1) - imported=2 = 1
        await Assert.That(counts.Errored).IsEqualTo(1);
        await Assert.That(counts.Excluded).IsEqualTo(0);
    }

    [Test]
    public async Task chain_vendor_with_all_imported_has_zero_errored() {
        var classifications = new[] {
            Cls("s1", "claude", ImportCommand.ClassificationStatus.New),
            Cls("s2", "claude", ImportCommand.ClassificationStatus.New),
        };

        var counts = ImportCommand.ComputePerSourceFinalCounts(
            classifications:  classifications,
            imported:         2,
            routedOutcomes:   null);

        await Assert.That(counts.Loaded).IsEqualTo(2);
        await Assert.That(counts.Errored).IsEqualTo(0);
    }

    // --- Multi-source invariant: sub-grid sum == totals ---

    [Test]
    public async Task multi_source_subgrid_sum_matches_aggregate_totals() {
        // Mixed run: Claude chain phase + Cursor routed phase.
        // Claude: 3 New, 2 imported, 1 errored.
        // Cursor: 4 New, 1 Loaded, 2 Skipped, 1 Failed.
        // Expected aggregate Excluded = 0 (Claude) + 2 (Cursor Skipped) = 2.
        // Expected aggregate Errored  = 1 (Claude) + 1 (Cursor Failed)  = 2.

        var claude = new[] {
            Cls("k1", "claude", ImportCommand.ClassificationStatus.New),
            Cls("k2", "claude", ImportCommand.ClassificationStatus.New),
            Cls("k3", "claude", ImportCommand.ClassificationStatus.New),
        };
        var cursor = new[] {
            Cls("c1", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c2", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c3", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c4", "cursor", ImportCommand.ClassificationStatus.New),
        };

        var claudeRow = ImportCommand.ComputePerSourceFinalCounts(claude, imported: 2, routedOutcomes: null);
        var cursorRow = ImportCommand.ComputePerSourceFinalCounts(cursor, imported: 1, routedOutcomes: (Loaded: 1, Skipped: 2, Failed: 1));

        await Assert.That(claudeRow.Loaded + cursorRow.Loaded).IsEqualTo(3);   // 2 + 1
        await Assert.That(claudeRow.Excluded + cursorRow.Excluded).IsEqualTo(2); // 0 + 2 (Skipped)
        await Assert.That(claudeRow.Errored + cursorRow.Errored).IsEqualTo(2);   // 1 + 1
    }

    // --- IsLifecycleOnlyRoutedReplay (vendor-NEUTRAL — no per-vendor scoping) ---
    //
    // The routed-phase loop in HandleImport increments routedLoaded/importedSessionIds whenever
    // ImportOutcome is Loaded/Resumed. For an AlreadyLoaded classification, ImportSessionAsync
    // re-asserts lifecycle hooks (repository backfill, C2) rather than importing new content, but
    // still returns Loaded/Resumed since there's nothing past the watermark to distinguish it by.
    // IsLifecycleOnlyRoutedReplay is the gate that keeps that replay from being double-counted on
    // top of the classify-time AlreadyLoaded bucket, and from joining importedSessionIds (which a
    // later --private pass would wrongly apply to).
    //
    // Round-2 follow-up findings broadened the gate two ways:
    //   (1) sentChildContent overrides the gate — an AlreadyLoaded parent that attached a
    //       brand-new nested child DID do real work and must not be suppressed.
    //   (2) AlreadyLoaded + Skipped (a correlated child whose own routed call short-circuits
    //       to Skipped because its parent imports it inline) is now also recognized, so it
    //       isn't double-counted as both Already-loaded and Excluded.
    //
    // Every routed vendor's lifecycle-only replay looks the same shape from here — there's no
    // `vendor` parameter to scope by.

    [Test]
    public async Task already_loaded_plus_loaded_outcome_is_a_lifecycle_only_replay() {
        var result = ImportCommand.IsLifecycleOnlyRoutedReplay(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Loaded, sentChildContent: false);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task already_loaded_plus_resumed_outcome_is_a_lifecycle_only_replay() {
        // The actual shape CursorImportSource.ImportSessionAsync returns for AlreadyLoaded: nothing
        // sent past a non-zero startLine (TotalLines) is classified Resumed, not Loaded.
        var result = ImportCommand.IsLifecycleOnlyRoutedReplay(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Resumed, sentChildContent: false);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task new_plus_loaded_outcome_is_not_a_lifecycle_only_replay() {
        // A genuine first-time import must still count as newly Loaded.
        var result = ImportCommand.IsLifecycleOnlyRoutedReplay(
            ImportCommand.ClassificationStatus.New, ImportOutcome.Loaded, sentChildContent: false);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task partial_plus_resumed_outcome_is_not_a_lifecycle_only_replay() {
        // A genuine resume of a partially-imported session must still count as newly Loaded.
        var result = ImportCommand.IsLifecycleOnlyRoutedReplay(
            ImportCommand.ClassificationStatus.Partial, ImportOutcome.Resumed, sentChildContent: false);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task already_loaded_plus_failed_outcome_is_not_a_lifecycle_only_replay() {
        // A failed lifecycle-reassert POST must still surface as Errored, not be swallowed.
        var result = ImportCommand.IsLifecycleOnlyRoutedReplay(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Failed, sentChildContent: false);

        await Assert.That(result).IsFalse();
    }

    // --- Round-2 finding 1: sentChildContent overrides the replay gate ---

    [Test]
    public async Task already_loaded_plus_resumed_outcome_with_sent_child_content_is_not_a_lifecycle_only_replay() {
        // An AlreadyLoaded parent whose own transcript has nothing new (outcome Resumed) can
        // still attach a brand-new, previously-unloaded nested child inline. That IS real work —
        // the replay gate must not suppress it, so the parent joins importedSessionIds/routedLoaded
        // and a later --private pass correctly re-privates it.
        var result = ImportCommand.IsLifecycleOnlyRoutedReplay(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Resumed, sentChildContent: true);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task already_loaded_plus_loaded_outcome_with_sent_child_content_is_not_a_lifecycle_only_replay() {
        var result = ImportCommand.IsLifecycleOnlyRoutedReplay(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Loaded, sentChildContent: true);

        await Assert.That(result).IsFalse();
    }

    // --- Round-2 finding 2: AlreadyLoaded + Skipped is now recognized ---

    [Test]
    public async Task already_loaded_plus_skipped_outcome_is_a_lifecycle_only_replay() {
        // A correlated nested child (its own status is AlreadyLoaded from a prior run) whose own
        // routed call short-circuits to Skipped because its parent imports it inline. This must
        // not double-count as both Already-loaded (classify time) and Excluded (routed outcome).
        var result = ImportCommand.IsLifecycleOnlyRoutedReplay(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Skipped, sentChildContent: false);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task new_plus_skipped_outcome_is_not_a_lifecycle_only_replay() {
        // A first-time-seen correlated child (status New, not AlreadyLoaded) whose own routed
        // call is Skipped still rolls up into the Excluded bucket — only the AlreadyLoaded case
        // is a double-count.
        var result = ImportCommand.IsLifecycleOnlyRoutedReplay(
            ImportCommand.ClassificationStatus.New, ImportOutcome.Skipped, sentChildContent: false);

        await Assert.That(result).IsFalse();
    }

    // --- ResolveRoutedOutcomeForCounting / IsSkippedChildContentOverride — the aggregation
    // boundary that decides what a non-suppressed routed call counts as. ---
    //
    // IsLifecycleOnlyRoutedReplay only ever says "suppress, don't count at all" — it was never
    // the thing that decided which bucket a non-suppressed call lands in (that's the raw
    // `outcome` switch downstream). That's exactly where a Skipped AlreadyLoaded call that
    // attached genuinely-new nested-child content (e.g. Antigravity's shape) falls through the
    // cracks: sentChildContent=true correctly stops the gate from suppressing it, but the
    // downstream switch keys off the literal `outcome`, and Skipped's non-suppressed branch
    // increments routedExcluded, not routedLoaded. These tests pin the resolver that fixes that.

    [Test]
    public async Task already_loaded_skipped_with_sent_child_content_overrides_to_loaded() {
        // The exact Antigravity shape: this is the case that was silently wrong before D2, and
        // the one no per-vendor test alone would catch.
        var isOverride = ImportCommand.IsSkippedChildContentOverride(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Skipped, sentChildContent: true);
        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Skipped, sentChildContent: true);

        await Assert.That(isOverride).IsTrue();
        await Assert.That(resolved).IsEqualTo(ImportOutcome.Loaded);
    }

    [Test]
    public async Task already_loaded_resumed_with_sent_child_content_resolves_unchanged() {
        // PRESERVED, not reclassified — IsSkippedChildContentOverride requires outcome==Skipped
        // exactly, so it never engages here; there's no ambiguity for the resolver to resolve.
        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Resumed, sentChildContent: true);

        await Assert.That(resolved).IsEqualTo(ImportOutcome.Resumed);
    }

    [Test]
    public async Task already_loaded_loaded_with_sent_child_content_resolves_unchanged() {
        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Loaded, sentChildContent: true);

        await Assert.That(resolved).IsEqualTo(ImportOutcome.Loaded);
    }

    [Test]
    public async Task already_loaded_failed_with_sent_child_content_resolves_to_failed_not_loaded() {
        // Content posting before a lifecycle failure must still surface as Errored — the
        // override's exact-match on Skipped excludes Failed by construction.
        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Failed, sentChildContent: true);

        await Assert.That(resolved).IsEqualTo(ImportOutcome.Failed);
    }

    [Test]
    public async Task already_loaded_loaded_without_sent_child_content_resolves_to_null_suppressed() {
        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Loaded, sentChildContent: false);

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task already_loaded_resumed_without_sent_child_content_resolves_to_null_suppressed() {
        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Resumed, sentChildContent: false);

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task already_loaded_skipped_without_sent_child_content_resolves_to_null_suppressed() {
        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            ImportCommand.ClassificationStatus.AlreadyLoaded, ImportOutcome.Skipped, sentChildContent: false);

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task new_plus_skipped_without_sent_child_content_resolves_to_own_raw_outcome() {
        // Never suppressed, never overridden — both the gate and the override require
        // AlreadyLoaded.
        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            ImportCommand.ClassificationStatus.New, ImportOutcome.Skipped, sentChildContent: false);

        await Assert.That(resolved).IsEqualTo(ImportOutcome.Skipped);
    }

    [Test]
    public async Task partial_plus_resumed_without_sent_child_content_resolves_to_own_raw_outcome() {
        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            ImportCommand.ClassificationStatus.Partial, ImportOutcome.Resumed, sentChildContent: false);

        await Assert.That(resolved).IsEqualTo(ImportOutcome.Resumed);
    }

    // --- importedSessionIds membership stays independent of `resolved` ---
    //
    // Membership is `resolved is not null && outcome is Loaded or Resumed` — NOT "raw outcome
    // regardless of sentChildContent", and NOT "resolved outcome". Both negatives below exercise
    // a different reason that naive phrasing would have been wrong.

    [Test]
    public async Task already_loaded_resumed_without_sent_child_content_does_not_join_imported_session_ids() {
        // resolved is null (suppressed by the gate) — even though the raw outcome is Resumed,
        // membership must be false. The imprecise "raw outcome regardless of sentChildContent"
        // phrasing would have gotten this wrong: it would have joined, contradicting the truth
        // table and changing non-Cursor --private behavior.
        const ImportCommand.ClassificationStatus status           = ImportCommand.ClassificationStatus.AlreadyLoaded;
        const ImportOutcome                      outcome          = ImportOutcome.Resumed;
        const bool                               sentChildContent = false;

        var resolved   = ImportCommand.ResolveRoutedOutcomeForCounting(status, outcome, sentChildContent);

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task already_loaded_skipped_with_sent_child_content_does_not_join_imported_session_ids() {
        // resolved is Loaded (the override fires), but the raw outcome is still Skipped, so
        // membership must be false — the Skipped-to-Loaded override is counting-only and
        // deliberately excluded from importedSessionIds / --private membership.
        const ImportCommand.ClassificationStatus status           = ImportCommand.ClassificationStatus.AlreadyLoaded;
        const ImportOutcome                      outcome          = ImportOutcome.Skipped;
        const bool                               sentChildContent = true;

        var resolved   = ImportCommand.ResolveRoutedOutcomeForCounting(status, outcome, sentChildContent);

        await Assert.That(resolved).IsEqualTo(ImportOutcome.Loaded);
    }
}
