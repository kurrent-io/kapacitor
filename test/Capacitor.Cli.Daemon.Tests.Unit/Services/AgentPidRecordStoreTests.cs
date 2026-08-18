using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Phase B (D4 §6.4(2)): <see cref="AgentPidRecordStore"/> — atomic write/read/delete round-trip
/// (exact identity preserved) and corrupt-record quarantine.
/// </summary>
public class AgentPidRecordStoreTests {
    static AgentPidRecord Rec(string agentId, int pid = 123) =>
        new(agentId, pid, "lx:boot:999", PidIdentityKind.Present, "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow);

    [Test]
    public async Task Write_ReadAll_Delete_roundtrip_preserves_exact_identity() {
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, NullLogger.Instance);

        store.Write(Rec("a1", pid: 4242));

        var all = store.ReadAll();
        await Assert.That(all).Count().IsEqualTo(1);
        await Assert.That(all[0].Pid).IsEqualTo(4242);
        await Assert.That(all[0].StartIdentity).IsEqualTo("lx:boot:999");
        await Assert.That(all[0].FlowRunId).IsEqualTo("flow-1");

        await Assert.That(store.Delete("a1")).IsTrue();
        await Assert.That(store.ReadAll()).IsEmpty();
        await Assert.That(store.Delete("a1")).IsFalse(); // idempotent
    }

    [Test]
    public async Task ReadAll_quarantines_a_corrupt_record_and_excludes_it() {
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, NullLogger.Instance);
        store.Write(Rec("good"));

        var agentsDir = tmp.PathTo("agents");
        File.WriteAllText(Path.Combine(agentsDir, "bad.json"), "{ not valid json");

        var all = store.ReadAll();

        await Assert.That(all.Select(r => r.AgentId)).IsEquivalentTo(new[] { "good" });
        await Assert.That(File.Exists(Path.Combine(agentsDir, "bad.json.corrupt"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(agentsDir, "bad.json"))).IsFalse();
    }

    [Test]
    public async Task Write_hashes_a_path_traversal_agent_id_inside_the_agents_dir() {
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, NullLogger.Instance);
        var agentsDir = tmp.PathTo("agents");

        // A hostile agent id (path separators / ".." — the id crosses the wire unconstrained) must not
        // escape the agents directory: the filename is a hash, and the record round-trips by its original id.
        store.Write(Rec("../../evil", pid: 7));

        var files = Directory.GetFiles(agentsDir, "*.json");
        await Assert.That(files.Length).IsEqualTo(1);
        await Assert.That(Path.GetFileName(files[0])).DoesNotContain("evil"); // hashed, not the raw id
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "../../evil")).IsTrue();
        await Assert.That(store.Delete("../../evil")).IsTrue();
        await Assert.That(store.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task Write_is_atomic_overwrite_no_temp_files_left() {
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, NullLogger.Instance);

        store.Write(Rec("a1", pid: 1));
        store.Write(Rec("a1", pid: 2)); // overwrite

        var all = store.ReadAll();
        await Assert.That(all).Count().IsEqualTo(1);
        await Assert.That(all[0].Pid).IsEqualTo(2);
        await Assert.That(Directory.EnumerateFiles(tmp.PathTo("agents"), "*.tmp-*")).IsEmpty();
    }

    [Test]
    public async Task ReadAll_decodes_a_legacy_record_with_no_identity_kind_key_as_present() {
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, NullLogger.Instance);

        // Build the JSON via the REAL serializer first (so this test can't drift from the actual
        // schema/casing), then surgically remove the identity_kind member — this produces the
        // EXACT old-shape JSON a pre-M1-A daemon actually wrote, not a hand-typed guess at the
        // property's wire name/casing.
        var current = new AgentPidRecord("legacy1", 999, "tk:123456789", PidIdentityKind.Present,
            "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow);
        var json = System.Text.Json.JsonSerializer.Serialize(current, CapacitorJsonContext.Default.AgentPidRecord);
        var legacyJson = System.Text.RegularExpressions.Regex.Replace(
            json, "\"identity_kind\"\\s*:\\s*\"?[A-Za-z]+\"?,?", "");

        await Assert.That(legacyJson).DoesNotContain("identity_kind");

        var agentsDir = tmp.CreateDir("agents");
        agentsDir.CreateFile("legacy.json", legacyJson);

        var all = store.ReadAll();
        await Assert.That(all.Select(r => r.AgentId)).IsEquivalentTo(new[] { "legacy1" });
        await Assert.That(all[0].IdentityKind).IsEqualTo(PidIdentityKind.Present);
        await Assert.That(all[0].StartIdentity).IsEqualTo("tk:123456789");
        // NOT quarantined — this is the whole point of the backward-compat contract.
        await Assert.That(File.Exists(agentsDir.PathTo("legacy.json.corrupt"))).IsFalse();
    }

    [Test]
    public async Task ReadAll_round_trips_an_identity_unavailable_record() {
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, NullLogger.Instance);

        store.Write(new AgentPidRecord("unresolved1", 42, "", PidIdentityKind.IdentityUnavailable,
            "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow));

        var all = store.ReadAll();
        await Assert.That(all).Count().IsEqualTo(1);
        await Assert.That(all[0].IdentityKind).IsEqualTo(PidIdentityKind.IdentityUnavailable);
        await Assert.That(all[0].StartIdentity).IsEmpty();
        await Assert.That(File.Exists(tmp.PathTo("agents", Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("unresolved1"))).ToLowerInvariant() + ".json.corrupt"))).IsFalse();
    }

    [Test]
    public async Task ReadAll_quarantines_present_with_empty_token_as_corrupt() {
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, NullLogger.Instance);

        // An inconsistent NEW shape — Present claims a comparable identity but the token is
        // empty. This is a real corruption signal (unlike the legacy missing-key case above),
        // so it must be quarantined, not silently accepted.
        store.Write(new AgentPidRecord("bad1", 1, "", PidIdentityKind.Present,
            "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow));

        var all = store.ReadAll();
        await Assert.That(all).IsEmpty();

        var agentsDir = tmp.PathTo("agents");
        var corruptFiles = Directory.GetFiles(agentsDir, "*.json.corrupt");
        await Assert.That(corruptFiles.Length).IsEqualTo(1);
    }

    [Test]
    public async Task ReadAll_quarantines_a_record_with_null_start_identity_without_throwing() {
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, NullLogger.Instance);
        var agentsDir = tmp.CreateDir("agents");

        // A parseable record whose start_identity is JSON null — System.Text.Json binds null to
        // the non-nullable positional string parameter, so ReadAll used to NRE on the subsequent
        // .Length check and abort the WHOLE sweep (OrphanReaper enumerates ReadAll() directly).
        // Build it from the real serializer, then null the token, so this can't drift from the
        // wire name/casing. AgentId stays non-empty and IdentityKind Present so the ONLY defect
        // is the null token.
        var current = new AgentPidRecord("nulltoken1", 5, "lx:boot:1", PidIdentityKind.Present,
            "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow);
        var json = System.Text.Json.JsonSerializer.Serialize(current, CapacitorJsonContext.Default.AgentPidRecord);
        var nulledJson = System.Text.RegularExpressions.Regex.Replace(
            json, "\"start_identity\"\\s*:\\s*\"[^\"]*\"", "\"start_identity\":null");
        await Assert.That(nulledJson).Contains("\"start_identity\":null");

        // Also drop a healthy record so we prove the sweep CONTINUES past the null one.
        store.Write(Rec("healthy"));
        agentsDir.CreateFile("nulltoken.json", nulledJson);

        var all = store.ReadAll(); // must NOT throw
        await Assert.That(all.Select(r => r.AgentId)).IsEquivalentTo(new[] { "healthy" });
        await Assert.That(File.Exists(agentsDir.PathTo("nulltoken.json.corrupt"))).IsTrue();
        await Assert.That(File.Exists(agentsDir.PathTo("nulltoken.json"))).IsFalse();
    }

    [Test]
    public async Task ReadAll_quarantines_a_record_with_an_unknown_identity_kind() {
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, NullLogger.Instance);
        var agentsDir = tmp.CreateDir("agents");

        // An out-of-range numeric identity_kind (99) is neither Present nor IdentityUnavailable, so
        // it passes BOTH consistency predicates and would be silently accepted without an explicit
        // Enum.IsDefined guard. Build via the real serializer, then rewrite the value to 99 (a raw
        // number JsonStringEnumConverter binds to (PidIdentityKind)99) so the wire name/casing can't
        // drift. AgentId + token stay well-formed so the ONLY defect is the unknown kind.
        var current = new AgentPidRecord("unknownkind1", 7, "lx:boot:1", PidIdentityKind.Present,
            "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow);
        var json = System.Text.Json.JsonSerializer.Serialize(current, CapacitorJsonContext.Default.AgentPidRecord);
        var unknownJson = System.Text.RegularExpressions.Regex.Replace(
            json, "\"identity_kind\"\\s*:\\s*\"[A-Za-z]+\"", "\"identity_kind\":99");
        await Assert.That(unknownJson).Contains("\"identity_kind\":99");

        // A healthy record alongside proves the sweep CONTINUES past the bad one.
        store.Write(Rec("healthy"));
        agentsDir.CreateFile("unknownkind.json", unknownJson);

        var all = store.ReadAll();
        await Assert.That(all.Select(r => r.AgentId)).IsEquivalentTo(new[] { "healthy" });
        await Assert.That(File.Exists(agentsDir.PathTo("unknownkind.json.corrupt"))).IsTrue();
        await Assert.That(File.Exists(agentsDir.PathTo("unknownkind.json"))).IsFalse();
    }

    [Test]
    public async Task ReadAll_quarantines_identity_unavailable_with_nonempty_token_as_corrupt() {
        using var tmp = new TempDir();
        var store = new AgentPidRecordStore(tmp.Path, NullLogger.Instance);

        store.Write(new AgentPidRecord("bad2", 1, "lx:boot:999", PidIdentityKind.IdentityUnavailable,
            "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow));

        var all = store.ReadAll();
        await Assert.That(all).IsEmpty();

        var corruptFiles = Directory.GetFiles(tmp.PathTo("agents"), "*.json.corrupt");
        await Assert.That(corruptFiles.Length).IsEqualTo(1);
    }
}
