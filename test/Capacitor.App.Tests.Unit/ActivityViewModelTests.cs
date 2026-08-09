using System.Globalization;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// Scripted `Func&lt;ConsentLogReadResult&gt;`: counts calls, can be reprogrammed between calls, and
/// can be armed to throw exactly once (test 7 — a throwing read must never escape the VM).
sealed class ScriptedReader {
    public int ReadCalls { get; private set; }
    public bool ThrowOnce;
    ConsentLogReadResult _result = new([], true);

    public void Set(ConsentLogReadResult result) => _result = result;

    public ConsentLogReadResult Read() {
        ReadCalls++;
        if (ThrowOnce) {
            ThrowOnce = false;
            throw new IOException("scripted read failure");
        }
        return _result;
    }
}

/// Scripted `Func&lt;string&gt;` stat key: settable between calls, can be armed to throw exactly once.
sealed class ScriptedStat {
    public string Key = "k0";
    public bool ThrowOnce;

    public string Get() {
        if (ThrowOnce) {
            ThrowOnce = false;
            throw new IOException("scripted stat failure");
        }
        return Key;
    }
}

/// Covers ActivityViewModel's row mapping and refresh semantics (spec §7, task-10-brief's 7
/// cases). No AvaloniaSession/RxSchedulers wrapping needed: the VM subscribes to the injected
/// ITicker directly with no ObserveOn (the ticker already delivers on the UI thread in
/// production — see ActivityViewModel's class doc comment), so a plain Subject-backed FakeTicker
/// delivers synchronously on the calling thread, same as AgentRowViewModel's tests.
public class ActivityViewModelTests {
    static ConsentDecisionRecord Rec(
            string decidedAt = "2026-08-08T12:00:00.0000000+00:00", string agentId = "a1", string? requester = "github:1",
            bool requesterIsOwner = false, string kind = "agent", string repoPath = "/repos/kcap-cli",
            string vendor = "claude", string outcome = "allowed", string source = "owner",
            string? requesterDisplay = null) =>
        new(decidedAt, agentId, requester, requesterIsOwner, kind, repoPath, vendor, outcome, source, requesterDisplay);

    // ---- 1: row mapping ----

    [Test]
    public async Task Rows_map_records_with_fallbacks_and_source_labels() {
        const string decidedAt = "2026-08-08T12:34:56.0000000+00:00";
        var expectedTime = DateTimeOffset.Parse(decidedAt, CultureInfo.InvariantCulture)
            .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var records = new[] {
            Rec(decidedAt: decidedAt, requester: "github:1", requesterDisplay: "Ada Lovelace",
                kind: "review-flow", repoPath: "/repos/kcap-cli/.claude/worktrees/tender-honking-pebble",
                vendor: "codex", outcome: "allowed", source: "rule[7]"),
            Rec(decidedAt: "not-a-timestamp", requester: null, requesterDisplay: null,
                kind: "agent", repoPath: "/repos/kcap-cli", vendor: "claude", outcome: "denied", source: "prompt_timeout"),
        };

        var reader = new ScriptedReader();
        reader.Set(new ConsentLogReadResult(records, true));
        var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());
        vm.OnTabVisibleChanged(true);

        var first = vm.Rows[0];
        await Assert.That(first.Time).IsEqualTo(expectedTime);
        await Assert.That(first.Requester).IsEqualTo("Ada Lovelace");
        await Assert.That(first.KindLabel).IsEqualTo("Review flow");
        await Assert.That(first.RepoLeaf).IsEqualTo("kcap-cli"); // worktree leaf stripped, RepoLabel
        await Assert.That(first.RepoFull).IsEqualTo("/repos/kcap-cli/.claude/worktrees/tender-honking-pebble");
        await Assert.That(first.Vendor).IsEqualTo("codex");
        await Assert.That(first.Outcome).IsEqualTo("allowed");
        await Assert.That(first.IsAllowed).IsTrue();
        await Assert.That(first.SourceLabel).IsEqualTo("rule");

        var second = vm.Rows[1];
        await Assert.That(second.Time).IsEqualTo("not-a-timestamp"); // unparseable -> verbatim
        await Assert.That(second.Requester).IsEqualTo("unknown"); // both requester and display absent
        await Assert.That(second.KindLabel).IsEqualTo("Agent");
        await Assert.That(second.Outcome).IsEqualTo("denied");
        await Assert.That(second.IsAllowed).IsFalse();
        await Assert.That(second.SourceLabel).IsEqualTo("timeout");
    }

    [Test]
    [Arguments("owner", "owner")]
    [Arguments("rule[7]", "rule")]
    [Arguments("default", "default policy")]
    [Arguments("prompt_user", "you")]
    [Arguments("prompt_timeout", "timeout")]
    [Arguments("prompt_no_ui", "no UI attached")]
    [Arguments("something-weird", "something-weird")] // unrecognized renders verbatim
    public async Task Source_labels(string source, string expected) {
        await Assert.That(ActivityViewModel.SourceLabelOf(source)).IsEqualTo(expected);
    }

    // ---- 2: Complete replaces, including to empty ----

    [Test]
    public async Task Complete_read_replaces_rows_including_to_empty() {
        var reader = new ScriptedReader();
        reader.Set(new ConsentLogReadResult([Rec(agentId: "a1"), Rec(agentId: "a2")], true));
        var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());

        vm.OnTabVisibleChanged(true);
        await Assert.That(vm.Rows.Count).IsEqualTo(2);
        await Assert.That(vm.IsEmpty).IsFalse();

        reader.Set(new ConsentLogReadResult([], true));
        vm.RequestRefresh();

        await Assert.That(vm.Rows.Count).IsEqualTo(0);
        await Assert.That(vm.IsEmpty).IsTrue();
    }

    // ---- 3: Incomplete keeps last-good; best-effort with nothing previous ----

    [Test]
    public async Task Incomplete_read_keeps_last_good_rows() {
        var reader = new ScriptedReader();
        reader.Set(new ConsentLogReadResult([Rec(agentId: "a1"), Rec(agentId: "a2")], true));
        var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());

        vm.OnTabVisibleChanged(true);
        await Assert.That(vm.Rows.Count).IsEqualTo(2);

        reader.Set(new ConsentLogReadResult([Rec(agentId: "a3")], false));
        vm.RequestRefresh();

        await Assert.That(vm.Rows.Count).IsEqualTo(2); // last-good kept, the partial read discarded
    }

    [Test]
    public async Task Incomplete_read_with_no_previous_rows_shows_partial_best_effort() {
        var reader = new ScriptedReader();
        reader.Set(new ConsentLogReadResult([Rec(agentId: "a1")], false));
        var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());

        vm.RequestRefresh(); // nothing displayed yet

        await Assert.That(vm.Rows.Count).IsEqualTo(1);
        await Assert.That(vm.IsEmpty).IsFalse();
    }

    // ---- 4: stat-gated poll, every 2nd tick while visible ----

    [Test]
    public async Task Stat_poll_rereads_only_on_change_every_2_ticks_while_visible() {
        var reader = new ScriptedReader();
        reader.Set(new ConsentLogReadResult([Rec()], true));
        var stat = new ScriptedStat { Key = "k0" };
        var ticker = new FakeTicker();
        var vm = new ActivityViewModel(reader.Read, stat.Get, ticker);

        vm.OnTabVisibleChanged(true); // immediate read
        await Assert.That(reader.ReadCalls).IsEqualTo(1);

        ticker.Tick();
        ticker.Tick(); // unchanged stat across the 2-tick window -> no re-read
        await Assert.That(reader.ReadCalls).IsEqualTo(1);

        stat.Key = "k1";
        ticker.Tick();
        ticker.Tick(); // changed stat, checked on the 2nd tick -> re-read
        await Assert.That(reader.ReadCalls).IsEqualTo(2);

        vm.OnTabVisibleChanged(false);
        stat.Key = "k2";
        ticker.Tick();
        ticker.Tick();
        ticker.Tick();
        ticker.Tick();
        await Assert.That(reader.ReadCalls).IsEqualTo(2); // invisible -> polling stopped entirely
    }

    // ---- 5: visibility triggers an immediate refresh ----

    [Test]
    public async Task Tab_visible_triggers_immediate_refresh() {
        var reader = new ScriptedReader();
        reader.Set(new ConsentLogReadResult([Rec()], true));
        var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());

        await Assert.That(reader.ReadCalls).IsEqualTo(0);
        vm.OnTabVisibleChanged(true);

        await Assert.That(reader.ReadCalls).IsEqualTo(1);
        await Assert.That(vm.Rows.Count).IsEqualTo(1);
    }

    // ---- 6: own-resolution refresh is an immediate read, eventual display ----

    [Test]
    public async Task Own_resolution_refresh_is_eventual() {
        var reader = new ScriptedReader();
        reader.Set(new ConsentLogReadResult([Rec(agentId: "a1")], true));
        var stat = new ScriptedStat { Key = "k0" };
        var ticker = new FakeTicker();
        var vm = new ActivityViewModel(reader.Read, stat.Get, ticker);

        vm.OnTabVisibleChanged(true);
        await Assert.That(vm.Rows.Count).IsEqualTo(1);

        // The ack fires RequestRefresh, but the daemon's own append hasn't landed yet — a stale
        // read is still what an immediate refresh can see.
        vm.RequestRefresh();
        await Assert.That(reader.ReadCalls).IsEqualTo(2);
        await Assert.That(vm.Rows.Count).IsEqualTo(1);

        // The append lands; the next stat-poll tick converges (spec: "no later than the next
        // poll", not "immediately").
        reader.Set(new ConsentLogReadResult([Rec(agentId: "a1"), Rec(agentId: "a2")], true));
        stat.Key = "k1";
        ticker.Tick();
        ticker.Tick();

        await Assert.That(vm.Rows.Count).IsEqualTo(2);
    }

    // ---- 7: a throwing stat or read is swallowed; polling keeps going ----

    [Test]
    public async Task Poll_survives_a_throwing_stat_or_read() {
        var reader = new ScriptedReader();
        reader.Set(new ConsentLogReadResult([Rec(agentId: "a1")], true));
        var stat = new ScriptedStat { Key = "k0" };
        var ticker = new FakeTicker();
        var vm = new ActivityViewModel(reader.Read, stat.Get, ticker);

        vm.OnTabVisibleChanged(true);
        await Assert.That(vm.Rows.Count).IsEqualTo(1);

        stat.ThrowOnce = true;
        Exception? caught = null;
        try { ticker.Tick(); ticker.Tick(); } catch (Exception ex) { caught = ex; }
        await Assert.That(caught).IsNull();

        // A later, healthy tick still drives the poll.
        stat.Key = "k1";
        reader.Set(new ConsentLogReadResult([Rec(agentId: "a1"), Rec(agentId: "a2")], true));
        ticker.Tick();
        ticker.Tick();
        await Assert.That(vm.Rows.Count).IsEqualTo(2);

        reader.ThrowOnce = true;
        try { vm.RequestRefresh(); } catch (Exception ex) { caught = ex; }
        await Assert.That(caught).IsNull();
        await Assert.That(vm.Rows.Count).IsEqualTo(2); // unaffected by the swallowed throw

        reader.Set(new ConsentLogReadResult([Rec(agentId: "a1")], true));
        vm.RequestRefresh();
        await Assert.That(vm.Rows.Count).IsEqualTo(1); // recovers on the next call
    }
}
