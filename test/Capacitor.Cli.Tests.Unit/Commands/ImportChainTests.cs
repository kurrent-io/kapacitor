using System.Globalization;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportChainTests {
    static ImportCommand.SessionClassification Classify(
        string id,
        ImportCommand.ClassificationStatus status,
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
        var classifications = new List<ImportCommand.SessionClassification> {
            Classify("a", ImportCommand.ClassificationStatus.New),
            Classify("b", ImportCommand.ClassificationStatus.AlreadyLoaded),
            Classify("c", ImportCommand.ClassificationStatus.Partial),
            Classify("d", ImportCommand.ClassificationStatus.TooShort),
            Classify("e", ImportCommand.ClassificationStatus.ProbeError),
            Classify("f", ImportCommand.ClassificationStatus.Excluded),
            Classify("g", ImportCommand.ClassificationStatus.InternalSubSession),
        };

        var chains = ImportCommand.BuildImportChains(classifications);

        var ids = chains.SelectMany(c => c).Select(c => c.SessionId).OrderBy(s => s).ToList();
        await Assert.That(ids).IsEquivalentTo(["a", "c"]);
    }

    [Test]
    public async Task BuildImportChains_groups_by_slug_and_orders_by_timestamp() {
        var classifications = new List<ImportCommand.SessionClassification> {
            Classify("a2", ImportCommand.ClassificationStatus.New, slug: "feature-x", ts: DateTimeOffset.Parse("2026-04-10T10:00:00Z", CultureInfo.InvariantCulture)),
            Classify("a1", ImportCommand.ClassificationStatus.New, slug: "feature-x", ts: DateTimeOffset.Parse("2026-04-10T09:00:00Z", CultureInfo.InvariantCulture)),
            Classify("a3", ImportCommand.ClassificationStatus.New, slug: "feature-x", ts: DateTimeOffset.Parse("2026-04-10T11:00:00Z", CultureInfo.InvariantCulture)),
        };

        var chains = ImportCommand.BuildImportChains(classifications);

        await Assert.That(chains.Count).IsEqualTo(1);
        // Order-sensitive: assert each position. IsEquivalentTo is permutation-
        // tolerant and would pass for any order, defeating the purpose of a
        // "orders by timestamp" test.
        var ids = chains[0].Select(c => c.SessionId).ToList();
        await Assert.That(ids.Count).IsEqualTo(3);
        await Assert.That(ids[0]).IsEqualTo("a1");
        await Assert.That(ids[1]).IsEqualTo("a2");
        await Assert.That(ids[2]).IsEqualTo("a3");
    }

    [Test]
    public async Task BuildImportChains_sessions_without_slug_are_singleton_chains() {
        var classifications = new List<ImportCommand.SessionClassification> {
            Classify("solo1", ImportCommand.ClassificationStatus.New),
            Classify("solo2", ImportCommand.ClassificationStatus.New),
        };

        var chains = ImportCommand.BuildImportChains(classifications);

        await Assert.That(chains.Count).IsEqualTo(2);
        await Assert.That(chains.All(c => c.Count == 1)).IsTrue();
    }
}
