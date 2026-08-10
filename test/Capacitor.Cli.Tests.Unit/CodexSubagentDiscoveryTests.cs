using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

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

    // ── TryReadMeta ───────────────────────────────────────────────────────

    [Test]
    public async Task TryReadMeta_Child_UsesOwnId_NotTheParentBearingSessionId() {
        var tmp = Directory.CreateTempSubdirectory("kcap-csd").FullName;
        try {
            var child = WriteChild(DayDir(tmp));

            var meta = CodexSubagentDiscovery.TryReadMeta(child);

            await Assert.That(meta).IsNotNull();
            await Assert.That(meta!.Value.IdDashless).IsEqualTo(Dashless(ChildDashed));
            await Assert.That(meta.Value.ParentThreadIdDashless).IsEqualTo(Dashless(ParentDashed));
            await Assert.That(meta.Value.IsSubagent).IsTrue();
            await Assert.That(meta.Value.AgentPath).IsEqualTo("/root/spec_quality");
            await Assert.That(meta.Value.AgentNickname).IsEqualTo("Hegel");
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task TryReadMeta_TopLevelSession_IsNotSubagent() {
        var tmp = Directory.CreateTempSubdirectory("kcap-csd").FullName;
        try {
            var parent = WriteParent(DayDir(tmp));

            var meta = CodexSubagentDiscovery.TryReadMeta(parent);

            await Assert.That(meta).IsNotNull();
            await Assert.That(meta!.Value.IsSubagent).IsFalse();
            await Assert.That(meta.Value.ParentThreadIdDashless).IsNull();
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task TryReadMeta_MidWriteHeader_ReturnsNull() {
        var tmp = Directory.CreateTempSubdirectory("kcap-csd").FullName;
        try {
            var path = Path.Combine(DayDir(tmp), $"rollout-2026-08-10T17-21-58-{ChildDashed}.jsonl");
            File.WriteAllText(path, """{"timestamp":"2026-08-10T15:21:59.231Z","type":"session_me""");

            await Assert.That(CodexSubagentDiscovery.TryReadMeta(path)).IsNull();
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── EnumerateSubagentRollouts ─────────────────────────────────────────

    [Test]
    public async Task EnumerateSubagentRollouts_FindsOwnChild_AndRulesOutForeigners() {
        var tmp = Directory.CreateTempSubdirectory("kcap-csd").FullName;
        try {
            var day    = DayDir(tmp);
            var parent = WriteParent(day);
            WriteChild(day);
            var other      = WriteParent(day, dashedId: OtherDashed);                    // unrelated top-level session
            var otherChild = WriteChild(day, dashedId: "019fec46-3858-7900-9d86-c6e79b604ac5",
                parentDashedId: OtherDashed, agentPath: "/root/standards_review", nickname: "Huygens",
                stamp: "2026-08-10T17-24-12");

            var ruledOut = new HashSet<string>(StringComparer.Ordinal);
            var subs     = CodexSubagentDiscovery.EnumerateSubagentRollouts(parent, Dashless(ParentDashed), ruledOut);

            await Assert.That(subs.Count).IsEqualTo(1);
            await Assert.That(subs[0].ChildDashlessId).IsEqualTo(Dashless(ChildDashed));
            await Assert.That(subs[0].AgentPath).IsEqualTo("/root/spec_quality");

            // Foreign rollouts are cached as definitive non-children; the parent's own child
            // is NOT ruled out (it stays enumerable for the teardown's fresh scan).
            await Assert.That(ruledOut.Contains(other)).IsTrue();
            await Assert.That(ruledOut.Contains(otherChild)).IsTrue();
            await Assert.That(ruledOut.Count).IsEqualTo(2);
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task EnumerateSubagentRollouts_MidWriteHeader_IsRetried_NotCached() {
        var tmp = Directory.CreateTempSubdirectory("kcap-csd").FullName;
        try {
            var day    = DayDir(tmp);
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
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task EnumerateSubagentRollouts_FindsChildInALaterDayDir() {
        var tmp = Directory.CreateTempSubdirectory("kcap-csd").FullName;
        try {
            var parent = WriteParent(DayDir(tmp));

            // Midnight rollover: the child spawns into the NEXT day's directory.
            var nextDay = Path.Combine(tmp, "2026", "08", "11");
            Directory.CreateDirectory(nextDay);
            WriteChild(nextDay, stamp: "2026-08-11T00-01-02");

            var subs = CodexSubagentDiscovery.EnumerateSubagentRollouts(parent, Dashless(ParentDashed));

            await Assert.That(subs.Count).IsEqualTo(1);
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── EnumerateDescendantRollouts ───────────────────────────────────────

    [Test]
    public async Task EnumerateDescendantRollouts_FlattensGrandchildrenUnderTheRoot() {
        var tmp = Directory.CreateTempSubdirectory("kcap-csd").FullName;
        try {
            var day    = DayDir(tmp);
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
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
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
