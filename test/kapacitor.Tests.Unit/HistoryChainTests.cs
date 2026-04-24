using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class HistoryChainTests {
    static HistoryCommand.SessionClassification Classify(
        string id,
        HistoryCommand.ClassificationStatus status,
        string? slug = null,
        DateTimeOffset? ts = null
    ) => new() {
        SessionId = id,
        FilePath = $"/tmp/{id}.jsonl",
        EncodedCwd = "-tmp",
        Meta = new SessionMetadata { Slug = slug, FirstTimestamp = ts ?? DateTimeOffset.UnixEpoch },
        Status = status,
    };

    [Test]
    public async Task BuildImportChains_includes_only_New_and_Partial() {
        var classifications = new List<HistoryCommand.SessionClassification> {
            Classify("a", HistoryCommand.ClassificationStatus.New),
            Classify("b", HistoryCommand.ClassificationStatus.AlreadyLoaded),
            Classify("c", HistoryCommand.ClassificationStatus.Partial),
            Classify("d", HistoryCommand.ClassificationStatus.TooShort),
            Classify("e", HistoryCommand.ClassificationStatus.ProbeError),
            Classify("f", HistoryCommand.ClassificationStatus.Excluded),
            Classify("g", HistoryCommand.ClassificationStatus.InternalSubSession),
        };

        var chains = HistoryCommand.BuildImportChains(classifications);

        var ids = chains.SelectMany(c => c).Select(c => c.SessionId).OrderBy(s => s).ToList();
        await Assert.That(ids).IsEquivalentTo(new[] { "a", "c" });
    }

    [Test]
    public async Task BuildImportChains_groups_by_slug_and_orders_by_timestamp() {
        var classifications = new List<HistoryCommand.SessionClassification> {
            Classify("a2", HistoryCommand.ClassificationStatus.New, slug: "feature-x", ts: DateTimeOffset.Parse("2026-04-10T10:00:00Z")),
            Classify("a1", HistoryCommand.ClassificationStatus.New, slug: "feature-x", ts: DateTimeOffset.Parse("2026-04-10T09:00:00Z")),
            Classify("a3", HistoryCommand.ClassificationStatus.New, slug: "feature-x", ts: DateTimeOffset.Parse("2026-04-10T11:00:00Z")),
        };

        var chains = HistoryCommand.BuildImportChains(classifications);

        await Assert.That(chains.Count).IsEqualTo(1);
        var ids = chains[0].Select(c => c.SessionId).ToList();
        await Assert.That(ids).IsEquivalentTo(new[] { "a1", "a2", "a3" });
    }

    [Test]
    public async Task BuildImportChains_sessions_without_slug_are_singleton_chains() {
        var classifications = new List<HistoryCommand.SessionClassification> {
            Classify("solo1", HistoryCommand.ClassificationStatus.New),
            Classify("solo2", HistoryCommand.ClassificationStatus.New),
        };

        var chains = HistoryCommand.BuildImportChains(classifications);

        await Assert.That(chains.Count).IsEqualTo(2);
        await Assert.That(chains.All(c => c.Count == 1)).IsTrue();
    }
}
