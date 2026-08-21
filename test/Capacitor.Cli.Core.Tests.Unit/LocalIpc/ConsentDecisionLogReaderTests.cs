using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class ConsentDecisionLogReaderTests {
    const string DaemonName = "test-daemon";

    // requesterDisplay: null omits the field entirely — the true old-format shape (spec §4.4's
    // field predates requester_display), distinct from an explicit JSON null.
    static string Record(string decidedAt, string agentId = "agent-1", string? requesterDisplay = "Alexey") {
        var displayField = requesterDisplay is null ? "" : $",\"requester_display\":\"{requesterDisplay}\"";
        return "{\"decided_at\":\"" + decidedAt + "\",\"agent_id\":\"" + agentId + "\"," +
               "\"requester\":\"alexey\",\"requester_is_owner\":true,\"kind\":\"agent\"," +
               "\"repo_path\":\"/repo\",\"vendor\":\"claude\",\"outcome\":\"allowed\"," +
               "\"source\":\"owner\"" + displayField + "}";
    }

    static void WriteLines(string path, params string[] lines) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + '\n');
    }

    [Test]
    public async Task Tail_merges_rotation_pair_newest_first_capped() {
        using var daemons = new TempDaemonStore();
        var path = daemons.Store.ConsentLogPath(DaemonName);
        WriteLines(path + ".1", Record("2026-01-01T00:00:00Z", "old-0"), Record("2026-01-01T00:00:01Z", "old-1"), Record("2026-01-01T00:00:02Z", "old-2"));
        WriteLines(path, Record("2026-01-01T00:01:00Z", "new-0"), Record("2026-01-01T00:01:01Z", "new-1"), Record("2026-01-01T00:01:02Z", "new-2"));

        var result = ConsentDecisionLogReader.ReadTail(daemons.Store, DaemonName, 4);

        await Assert.That(result.Complete).IsTrue();
        await Assert.That(result.Records.Count).IsEqualTo(4);
        await Assert.That(result.Records[0].AgentId).IsEqualTo("new-2"); // current file's last record first
        await Assert.That(result.Records[1].AgentId).IsEqualTo("new-1");
        await Assert.That(result.Records[2].AgentId).IsEqualTo("new-0");
        await Assert.That(result.Records[3].AgentId).IsEqualTo("old-2");
    }

    [Test]
    public async Task Undecodable_and_structurally_invalid_lines_are_skipped() {
        using var daemons = new TempDaemonStore();
        var path = daemons.Store.ConsentLogPath(DaemonName);
        WriteLines(path,
            "not-json",
            Record("2026-01-01T00:00:00Z", "valid-1"),
            "{}",
            Record("2026-01-01T00:00:01Z", "valid-2"));

        var result = ConsentDecisionLogReader.ReadTail(daemons.Store, DaemonName, 10);

        await Assert.That(result.Complete).IsTrue();
        await Assert.That(result.Records.Count).IsEqualTo(2);
        await Assert.That(result.Records[0].AgentId).IsEqualTo("valid-2");
        await Assert.That(result.Records[1].AgentId).IsEqualTo("valid-1");
    }

    [Test]
    public async Task Absent_files_are_a_complete_empty_read() {
        using var daemons = new TempDaemonStore();
        var result = ConsentDecisionLogReader.ReadTail(daemons.Store, DaemonName, 10);

        await Assert.That(result.Records).IsEmpty();
        await Assert.That(result.Complete).IsTrue();
    }

    [Test]
    public async Task Unreadable_file_flips_complete_false_with_partial_records() {
        using var daemons = new TempDaemonStore();
        var path = daemons.Store.ConsentLogPath(DaemonName);
        WriteLines(path + ".1", Record("2026-01-01T00:00:00Z", "backup-0"));

        // Cross-platform stand-in for "unreadable": a directory at the exact file path makes
        // the production FileStream open throw UnauthorizedAccessException/IOException,
        // without relying on Windows-only exclusive-sharing semantics.
        Directory.CreateDirectory(path);

        var result = ConsentDecisionLogReader.ReadTail(daemons.Store, DaemonName, 10);

        await Assert.That(result.Complete).IsFalse();
        await Assert.That(result.Records.Count).IsEqualTo(1);
        await Assert.That(result.Records[0].AgentId).IsEqualTo("backup-0");
    }

    [Test]
    public async Task Old_format_lines_parse_with_null_display() {
        using var daemons = new TempDaemonStore();
        var path = daemons.Store.ConsentLogPath(DaemonName);
        WriteLines(path, Record("2026-01-01T00:00:00Z", "no-display", requesterDisplay: null));

        var result = ConsentDecisionLogReader.ReadTail(daemons.Store, DaemonName, 10);

        await Assert.That(result.Complete).IsTrue();
        await Assert.That(result.Records.Count).IsEqualTo(1);
        await Assert.That(result.Records[0].RequesterDisplay).IsNull();
    }

    [Test]
    public async Task Duplicate_records_across_the_rotation_boundary_are_deduped() {
        using var daemons = new TempDaemonStore();
        var path = daemons.Store.ConsentLogPath(DaemonName);
        var shared = Record("2026-01-01T00:00:00Z", "shared");
        WriteLines(path + ".1", shared);
        WriteLines(path, shared);

        var result = ConsentDecisionLogReader.ReadTail(daemons.Store, DaemonName, 10);

        await Assert.That(result.Complete).IsTrue();
        await Assert.That(result.Records.Count).IsEqualTo(1);
        await Assert.That(result.Records[0].AgentId).IsEqualTo("shared");
    }

    [Test]
    public async Task Reader_share_mode_never_blocks_the_writer() {
        using var daemons = new TempDaemonStore();
        var path = daemons.Store.ConsentLogPath(DaemonName);
        WriteLines(path, Record("2026-01-01T00:00:00Z", "initial"));

        using var readerHandle = ConsentDecisionLogReader.OpenShared(path);

        // (a) the daemon writer's append mode must not be blocked by the open reader handle.
        var appendLine = Record("2026-01-01T00:00:01Z", "appended") + '\n';
        await using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write)) {
            var bytes = System.Text.Encoding.UTF8.GetBytes(appendLine);
            await writer.WriteAsync(bytes);
        }

        // (b) the daemon's rotation (File.Move) must not be blocked either — this is what
        // FileShare.Delete buys.
        File.Move(path, path + ".1", overwrite: true);

        // (c) a concurrent ReadTail must succeed while the original handle is still open.
        var result = ConsentDecisionLogReader.ReadTail(daemons.Store, DaemonName, 10);
        await Assert.That(result.Complete).IsTrue();
        await Assert.That(result.Records.Count).IsEqualTo(2);
    }
}
