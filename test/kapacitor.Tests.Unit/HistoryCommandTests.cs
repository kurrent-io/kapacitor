using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class HistoryCommandTests {
    // --- ExtractLastTimestamp ---

    [Test]
    public async Task ExtractLastTimestamp_returns_last_timestamp_from_jsonl() {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllLinesAsync(path, [
                """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"hello"}}""",
                """{"type":"assistant","timestamp":"2026-03-15T10:01:00Z","message":{"content":"hi"}}""",
                """{"type":"user","timestamp":"2026-03-15T10:05:00Z","message":{"content":"bye"}}""",
            ]);

            var result = HistoryCommand.ExtractLastTimestamp(path);

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Value).IsEqualTo(DateTimeOffset.Parse("2026-03-15T10:05:00Z"));
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExtractLastTimestamp_skips_lines_without_timestamp() {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllLinesAsync(path, [
                """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"hello"}}""",
                """{"type":"file-history-snapshot","files":[]}""",
            ]);

            var result = HistoryCommand.ExtractLastTimestamp(path);

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Value).IsEqualTo(DateTimeOffset.Parse("2026-03-15T10:00:00Z"));
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExtractLastTimestamp_returns_null_for_empty_file() {
        var path = Path.GetTempFileName();

        try {
            var result = HistoryCommand.ExtractLastTimestamp(path);
            await Assert.That(result).IsNull();
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExtractLastTimestamp_returns_null_for_missing_file() {
        var result = HistoryCommand.ExtractLastTimestamp("/tmp/nonexistent-" + Guid.NewGuid() + ".jsonl");
        await Assert.That(result).IsNull();
    }

    // --- ExtractSessionMetadata ---

    [Test]
    public async Task ExtractSessionMetadata_extracts_first_timestamp() {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllLinesAsync(path, [
                """{"type":"user","timestamp":"2026-03-15T09:30:00Z","cwd":"/home/user/project","message":{"content":"hello"}}""",
                """{"type":"assistant","timestamp":"2026-03-15T09:31:00Z","message":{"model":"opus","content":"hi"}}""",
            ]);

            var meta = HistoryCommand.ExtractSessionMetadata(path);

            await Assert.That(meta.FirstTimestamp).IsNotNull();
            await Assert.That(meta.FirstTimestamp!.Value).IsEqualTo(DateTimeOffset.Parse("2026-03-15T09:30:00Z"));
            await Assert.That(meta.Cwd).IsEqualTo("/home/user/project");
            await Assert.That(meta.Model).IsEqualTo("opus");
        } finally {
            File.Delete(path);
        }
    }
}
