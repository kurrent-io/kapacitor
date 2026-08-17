using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

/// <summary>
/// Unit tests for <see cref="CodexSubagentDiscovery"/> — the shared discovery used by the
/// live watcher scan (<c>WatchCommand.ScanCodexSubagents</c>), the parent-exit teardown and
/// the import descendant walk: locate collab subagent rollouts in the shared
/// <c>~/.codex/sessions/YYYY/MM/DD</c> tree via their <c>session_meta</c> linkage
/// (<c>thread_source: "subagent"</c> + <c>parent_thread_id</c>), never keying a child by its
/// <c>session_id</c> field (which holds the PARENT's id — the child's own id is in <c>id</c>).
/// </summary>
public class CodexSubagentDiscoveryTests {
    const string ParentDashed = "019fec43-21ea-7542-bbde-4a9dfe352e81";
    const string ChildDashed  = "019fec44-2cff-7d70-9806-78a49595f393";
    const string OtherDashed  = "019fec44-e819-72c2-bb86-e18efa92e18c";

    static string Dashless(string dashed) => dashed.Replace("-", "");

    static string DayDir(string root) {
        var day = Path.Combine(root, "2026", "08", "10");
        Directory.CreateDirectory(day);

        return day;
    }

    static string WriteParent(string dayDir, string dashedId = ParentDashed, string cwd = "/w") {
        var path = Path.Combine(dayDir, $"rollout-2026-08-10T17-20-50-{dashedId}.jsonl");
        File.WriteAllText(path,
            "{\"timestamp\":\"2026-08-10T15:20:50.000Z\",\"type\":\"session_meta\",\"payload\":{"
          + $"\"session_id\":\"{dashedId}\",\"id\":\"{dashedId}\",\"cwd\":\"{cwd}\","
          + "\"originator\":\"codex-tui\",\"cli_version\":\"0.146.0\",\"source\":\"cli\","
          + "\"thread_source\":\"user\",\"model_provider\":\"openai\"}}\n");

        return path;
    }

    static string WriteChild(
            string dayDir,
            string dashedId       = ChildDashed,
            string parentDashedId = ParentDashed,
            string agentPath      = "/root/spec_quality",
            string nickname       = "Hegel",
            string stamp          = "2026-08-10T17-21-58"
        ) {
        var path = Path.Combine(dayDir, $"rollout-{stamp}-{dashedId}.jsonl");

        // Real 0.146.0 shape — note session_id carries the PARENT's id, the child's own id
        // is in `id`, and the subagent linkage rides thread_source + parent_thread_id.
        File.WriteAllText(path,
            "{\"timestamp\":\"2026-08-10T15:21:59.231Z\",\"type\":\"session_meta\",\"payload\":{"
          + $"\"session_id\":\"{parentDashedId}\",\"id\":\"{dashedId}\","
          + $"\"forked_from_id\":\"{parentDashedId}\",\"parent_thread_id\":\"{parentDashedId}\","
          + "\"cwd\":\"/w\",\"originator\":\"codex-tui\",\"cli_version\":\"0.146.0\","
          + "\"source\":{\"subagent\":{\"thread_spawn\":{"
          + $"\"parent_thread_id\":\"{parentDashedId}\",\"depth\":1,"
          + $"\"agent_path\":\"{agentPath}\",\"agent_nickname\":\"{nickname}\",\"agent_role\":null}}}}}},"
          + $"\"thread_source\":\"subagent\",\"agent_nickname\":\"{nickname}\","
          + $"\"agent_path\":\"{agentPath}\",\"model_provider\":\"openai\"}}}}\n");

        return path;
    }

    // ── ReadHeader ────────────────────────────────────────────────────────

    [Test]
    public async Task ReadHeader_Child_UsesOwnId_NotTheParentBearingSessionId() {
        using var tmp = new TempDir();
        var child = WriteChild(DayDir(tmp.Path));

        var (outcome, meta) = CodexSubagentDiscovery.ReadHeader(child);

        await Assert.That(outcome).IsEqualTo(CodexSubagentDiscovery.RolloutHeader.Subagent);
        await Assert.That(meta.IdDashless).IsEqualTo(Dashless(ChildDashed));
        await Assert.That(meta.ParentThreadIdDashless).IsEqualTo(Dashless(ParentDashed));
        await Assert.That(meta.AgentPath).IsEqualTo("/root/spec_quality");
        await Assert.That(meta.AgentNickname).IsEqualTo("Hegel");
    }

    [Test]
    public async Task ReadHeader_TopLevelSession_IsNotSubagent() {
        using var tmp = new TempDir();
        var parent = WriteParent(DayDir(tmp.Path));

        var (outcome, meta) = CodexSubagentDiscovery.ReadHeader(parent);

        await Assert.That(outcome).IsEqualTo(CodexSubagentDiscovery.RolloutHeader.NotSubagent);
        await Assert.That(meta.ParentThreadIdDashless).IsNull();
    }

    [Test]
    public async Task ReadHeader_MidWriteHeader_IsIndeterminate() {
        // Truncated first line (no newline yet) — could still become a valid session_meta.
        using var tmp = new TempDir();
        var path = Path.Combine(DayDir(tmp.Path), $"rollout-2026-08-10T17-21-58-{ChildDashed}.jsonl");
        File.WriteAllText(path, """{"timestamp":"2026-08-10T15:21:59.231Z","type":"session_me""");

        await Assert.That(CodexSubagentDiscovery.ReadHeader(path).Outcome)
            .IsEqualTo(CodexSubagentDiscovery.RolloutHeader.Indeterminate);
    }

    [Test]
    public async Task ReadHeader_EmptyFile_IsIndeterminate() {
        using var tmp = new TempDir();
        var path = Path.Combine(DayDir(tmp.Path), $"rollout-2026-08-10T17-21-58-{ChildDashed}.jsonl");
        File.WriteAllText(path, "");

        await Assert.That(CodexSubagentDiscovery.ReadHeader(path).Outcome)
            .IsEqualTo(CodexSubagentDiscovery.RolloutHeader.Indeterminate);
    }

    [Test]
    public async Task ReadHeader_CompleteGarbageLine_IsDefinitivelyNotSubagent() {
        // A newline-terminated first line that isn't parseable session_meta is a permanently
        // malformed file — it must classify definitively so polling callers can cache it as
        // ruled-out instead of re-opening it on every tick.
        using var tmp = new TempDir();
        var path = Path.Combine(DayDir(tmp.Path), $"rollout-2026-08-10T17-21-58-{ChildDashed}.jsonl");
        File.WriteAllText(path, "this is not json\n");

        await Assert.That(CodexSubagentDiscovery.ReadHeader(path).Outcome)
            .IsEqualTo(CodexSubagentDiscovery.RolloutHeader.NotSubagent);
    }

    [Test]
    public async Task ReadHeader_CompleteNonSessionMetaJson_IsDefinitivelyNotSubagent() {
        using var tmp = new TempDir();
        var path = Path.Combine(DayDir(tmp.Path), $"rollout-2026-08-10T17-21-58-{ChildDashed}.jsonl");
        File.WriteAllText(path, """{"type":"event_msg","payload":{"type":"task_started"}}""" + "\n");

        await Assert.That(CodexSubagentDiscovery.ReadHeader(path).Outcome)
            .IsEqualTo(CodexSubagentDiscovery.RolloutHeader.NotSubagent);
    }

    [Test]
    public async Task ReadHeader_TruncatedButCompleteJson_IsJudgedOnContent() {
        // The writer flushed the whole session_meta JSON but not the trailing newline yet —
        // parseable content wins over line-completeness.
        using var tmp = new TempDir();
        var child = WriteChild(DayDir(tmp.Path));
        var text  = File.ReadAllText(child).TrimEnd('\n');
        File.WriteAllText(child, text); // strip the newline

        await Assert.That(CodexSubagentDiscovery.ReadHeader(child).Outcome)
            .IsEqualTo(CodexSubagentDiscovery.RolloutHeader.Subagent);
    }

    // ── EnumerateSubagentRollouts ─────────────────────────────────────────

    [Test]
    public async Task EnumerateSubagentRollouts_FindsOwnChild_AndRulesOutForeigners() {
        using var tmp = new TempDir();
        var day    = DayDir(tmp.Path);
        var parent = WriteParent(day);
        WriteChild(day);
        var other      = WriteParent(day, dashedId: OtherDashed);                    // unrelated top-level session
        var otherChild = WriteChild(day, dashedId: "019fec46-3858-7900-9d86-c6e79b604ac5",
            parentDashedId: OtherDashed, agentPath: "/root/standards_review", nickname: "Huygens",
            stamp: "2026-08-10T17-24-12");
        var garbage = Path.Combine(day, "rollout-2026-08-10T17-25-00-019fec46-51e6-7a03-9f0b-cc6f701a9134.jsonl");
        File.WriteAllText(garbage, "not json at all\n"); // permanently malformed — must be ruled out, not re-read forever

        var ruledOut = new HashSet<string>(StringComparer.Ordinal);
        var subs     = CodexSubagentDiscovery.EnumerateSubagentRollouts(parent, Dashless(ParentDashed), ruledOut);

        await Assert.That(subs.Count).IsEqualTo(1);
        await Assert.That(subs[0].ChildDashlessId).IsEqualTo(Dashless(ChildDashed));
        await Assert.That(subs[0].AgentPath).IsEqualTo("/root/spec_quality");

        // Foreign/malformed rollouts are cached as definitive non-children; the parent's
        // own child is NOT ruled out (it stays enumerable for the teardown's fresh scan).
        await Assert.That(ruledOut.Contains(other)).IsTrue();
        await Assert.That(ruledOut.Contains(otherChild)).IsTrue();
        await Assert.That(ruledOut.Contains(garbage)).IsTrue();
        await Assert.That(ruledOut.Count).IsEqualTo(3);
    }

    [Test]
    public async Task EnumerateSubagentRollouts_MidWriteHeader_IsRetried_NotCached() {
        using var tmp = new TempDir();
        var day    = DayDir(tmp.Path);
        var parent = WriteParent(day);

        // First tick: the child rollout exists but its header line has no newline-complete
        // session_meta yet — it must be skipped WITHOUT being ruled out.
        var childPath = Path.Combine(day, $"rollout-2026-08-10T17-21-58-{ChildDashed}.jsonl");
        File.WriteAllText(childPath, """{"timestamp":"2026-08-10T15:21:59.231Z","type":"session_me""");

        var ruledOut = new HashSet<string>(StringComparer.Ordinal);
        var first    = CodexSubagentDiscovery.EnumerateSubagentRollouts(parent, Dashless(ParentDashed), ruledOut);

        await Assert.That(first.Count).IsEqualTo(0);
        await Assert.That(ruledOut.Contains(childPath)).IsFalse();

        // Second tick: header complete — now discovered.
        WriteChild(day);
        var second = CodexSubagentDiscovery.EnumerateSubagentRollouts(parent, Dashless(ParentDashed), ruledOut);

        await Assert.That(second.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EnumerateSubagentRollouts_FindsChildInALaterDayDir() {
        using var tmp = new TempDir();
        var parent = WriteParent(DayDir(tmp.Path));

        // Midnight rollover: the child spawns into the NEXT day's directory.
        var nextDay = tmp.PathTo("2026", "08", "11");
        Directory.CreateDirectory(nextDay);
        WriteChild(nextDay, stamp: "2026-08-11T00-01-02");

        var subs = CodexSubagentDiscovery.EnumerateSubagentRollouts(parent, Dashless(ParentDashed));

        await Assert.That(subs.Count).IsEqualTo(1);
    }

    // ── EnumerateDescendantRollouts ───────────────────────────────────────

    [Test]
    public async Task EnumerateDescendantRollouts_FlattensGrandchildrenUnderTheRoot() {
        using var tmp = new TempDir();
        var day    = DayDir(tmp.Path);
        var parent = WriteParent(day);
        WriteChild(day);

        const string grandDashed = "019fec46-51e6-7a03-9f0b-cc6f701a9134";
        WriteChild(day, dashedId: grandDashed, parentDashedId: ChildDashed,
            agentPath: "/root/spec_quality/deep_dive", nickname: "Confucius",
            stamp: "2026-08-10T17-25-00");

        var subs = CodexSubagentDiscovery.EnumerateDescendantRollouts(parent, Dashless(ParentDashed));

        await Assert.That(subs.Count).IsEqualTo(2);
        await Assert.That(subs.Select(s => s.ChildDashlessId).ToList())
            .Contains(Dashless(ChildDashed));
        await Assert.That(subs.Select(s => s.ChildDashlessId).ToList())
            .Contains(Dashless(grandDashed));
    }

    // ── CodexImportSource.DiscoverAsync filter ────────────────────────────

    [Test]
    public async Task ImportDiscovery_ExcludesSubagentAndIndeterminate_KeepsTopLevelAndMalformed() {
        // Top-level discovery must contain ONLY definitive non-subagents: a child rollout
        // imports nested under its parent (never as an unrelated top-level session), and a
        // rollout whose header can't be judged yet (actively starting mid-import) is skipped
        // for this pass instead of being imported top-level now and nested on the next run.
        // A permanently malformed header stays importable — it can never be proven a child.
        using var tmp = new TempDir();
        var day = DayDir(tmp.Path);
        WriteParent(day);
        WriteChild(day);

        const string truncatedDashed = "019fec46-3858-7900-9d86-c6e79b604ac5";
        File.WriteAllText(
            Path.Combine(day, $"rollout-2026-08-10T17-24-12-{truncatedDashed}.jsonl"),
            """{"timestamp":"2026-08-10T15:24:12.000Z","type":"session_me""");

        const string malformedDashed = "019fec46-51e6-7a03-9f0b-cc6f701a9134";
        File.WriteAllText(
            Path.Combine(day, $"rollout-2026-08-10T17-24-19-{malformedDashed}.jsonl"),
            "not json at all\n");

        var source     = new CodexImportSource(rootOverride: tmp.Path);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var ids        = discovered.Select(d => d.SessionId).OrderBy(x => x, StringComparer.Ordinal).ToList();

        await Assert.That(ids).IsEquivalentTo(new[] {
            Dashless(ParentDashed),
            Dashless(malformedDashed),
        });
    }

    // ── AgentTypeFrom ─────────────────────────────────────────────────────

    [Test]
    [Arguments("/root/spec_quality", "Hegel", "spec_quality")]
    [Arguments("/root/a/b/implementation_feasibility", "Zeno", "implementation_feasibility")]
    [Arguments(null, "Hegel", "Hegel")]
    [Arguments(null, null, "subagent")]
    [Arguments("", "", "subagent")]
    public async Task AgentTypeFrom_PrefersPathLeaf_ThenNickname_ThenFallback(string? path, string? nickname, string expected) {
        await Assert.That(CodexSubagentDiscovery.AgentTypeFrom(path, nickname)).IsEqualTo(expected);
    }
}
