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

/// A `Func&lt;ConsentLogReadResult&gt;` that blocks on a caller-controlled gate — lets a test hold a
/// read genuinely "in flight" on the thread pool, deterministically and without a sleep, to
/// exercise ActivityViewModel's single-flight guard (test 9).
sealed class GatedReader {
    public int ReadCalls { get; private set; }
    readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ConsentLogReadResult _result = new([], true);

    public Task Started => _started.Task;
    public void Set(ConsentLogReadResult result) => _result = result;
    public void Release() => _release.SetResult();

    public ConsentLogReadResult Read() {
        ReadCalls++;
        _started.TrySetResult();
        _release.Task.GetAwaiter().GetResult(); // blocks the background thread, never the UI thread
        return _result;
    }
}

/// Covers ActivityViewModel's row mapping and refresh semantics (spec §7, task-10-brief's 7 cases),
/// including the stat+read hop off the UI thread. The VM now hops through Task.Run and back via
/// Dispatcher.UIThread.InvokeAsync, so every test that triggers a refresh
/// runs under AvaloniaSession (the real headless dispatcher) and awaits
/// ActivityViewModel.PendingRefreshForTesting — the same completion the production single-flight
/// guard watches — instead of guessing at a delay. A plain Subject-backed FakeTicker still delivers
/// Tick() synchronously on the calling thread; only the VM's OWN work moved off-thread.
public class ActivityViewModelTests {
    static ConsentDecisionRecord Rec(
            string decidedAt = "2026-08-08T12:00:00.0000000+00:00", string agentId = "a1", string? requester = "github:1",
            bool requesterIsOwner = false, string kind = "agent", string repoPath = "/repos/kcap-cli",
            string vendor = "claude", string outcome = "allowed", string source = "owner",
            string? requesterDisplay = null) =>
        new(decidedAt, agentId, requester, requesterIsOwner, kind, repoPath, vendor, outcome, source, requesterDisplay);

    // ---- 1: row mapping ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rows_map_records_with_fallbacks_and_source_labels() {
        const string decidedAt = "2026-08-08T12:34:56.0000000+00:00";
        var local = DateTimeOffset.Parse(decidedAt, CultureInfo.InvariantCulture).ToLocalTime();
        var expectedTime = local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
        var expectedTip = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var records = new[] {
            Rec(decidedAt: decidedAt, requester: "github:1", requesterDisplay: "Ada Lovelace",
                kind: "review-flow", repoPath: "/repos/kcap-cli/.claude/worktrees/tender-honking-pebble",
                vendor: "codex", outcome: "allowed", source: "rule[7]"),
            Rec(decidedAt: "not-a-timestamp", requester: null, requesterDisplay: null,
                kind: "agent", repoPath: "/repos/kcap-cli", vendor: "claude", outcome: "denied", source: "prompt_timeout"),
        };

        var (first, second) = await AvaloniaSession.DispatchAsync(async () => {
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult(records, true));
            var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());
            vm.OnTabVisibleChanged(true);
            await vm.PendingRefreshForTesting!;

            return (vm.Rows[0], vm.Rows[1]);
        });

        await Assert.That(first.Time).IsEqualTo(expectedTime);
        await Assert.That(first.TimeTip).IsEqualTo(expectedTip);
        await Assert.That(first.Requester).IsEqualTo("Ada Lovelace");
        await Assert.That(first.KindLabel).IsEqualTo("Review flow");
        // A consent record carries the launch request's path verbatim, with no repository behind
        // it on the wire, so the leaf is that path's own; RepoFull carries the rest.
        await Assert.That(first.RepoLeaf).IsEqualTo("tender-honking-pebble");
        await Assert.That(first.RepoFull).IsEqualTo("/repos/kcap-cli/.claude/worktrees/tender-honking-pebble");
        await Assert.That(first.Vendor).IsEqualTo("codex");
        await Assert.That(first.Outcome).IsEqualTo("allowed");
        await Assert.That(first.IsAllowed).IsTrue();
        await Assert.That(first.SourceLabel).IsEqualTo("rule");

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
    [NotInParallel("AvaloniaSession")]
    public async Task Complete_read_replaces_rows_including_to_empty() {
        var (countAfterFirst, emptyAfterFirst, countAfterSecond, emptyAfterSecond) = await AvaloniaSession.DispatchAsync(async () => {
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([Rec(agentId: "a1"), Rec(agentId: "a2")], true));
            var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());

            vm.OnTabVisibleChanged(true);
            await vm.PendingRefreshForTesting!;
            var countAfterFirst = vm.Rows.Count;
            var emptyAfterFirst = vm.IsEmpty;

            reader.Set(new ConsentLogReadResult([], true));
            vm.RequestRefresh();
            await vm.PendingRefreshForTesting!;

            return (countAfterFirst, emptyAfterFirst, vm.Rows.Count, vm.IsEmpty);
        });

        await Assert.That(countAfterFirst).IsEqualTo(2);
        await Assert.That(emptyAfterFirst).IsFalse();
        await Assert.That(countAfterSecond).IsEqualTo(0);
        await Assert.That(emptyAfterSecond).IsTrue();
    }

    // ---- 3: Incomplete keeps last-good; best-effort with nothing previous ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Incomplete_read_keeps_last_good_rows() {
        var count = await AvaloniaSession.DispatchAsync(async () => {
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([Rec(agentId: "a1"), Rec(agentId: "a2")], true));
            var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());

            vm.OnTabVisibleChanged(true);
            await vm.PendingRefreshForTesting!;

            reader.Set(new ConsentLogReadResult([Rec(agentId: "a3")], false));
            vm.RequestRefresh();
            await vm.PendingRefreshForTesting!;

            return vm.Rows.Count;
        });

        await Assert.That(count).IsEqualTo(2); // last-good kept, the partial read discarded
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Incomplete_read_with_no_previous_rows_shows_partial_best_effort() {
        var (count, isEmpty) = await AvaloniaSession.DispatchAsync(async () => {
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([Rec(agentId: "a1")], false));
            var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());

            vm.RequestRefresh(); // nothing displayed yet
            await vm.PendingRefreshForTesting!;

            return (vm.Rows.Count, vm.IsEmpty);
        });

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(isEmpty).IsFalse();
    }

    // ---- 4: stat-gated poll, every 2nd tick while visible ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Stat_poll_rereads_only_on_change_every_2_ticks_while_visible() {
        var (afterVisible, afterUnchanged, afterChanged, afterInvisible) = await AvaloniaSession.DispatchAsync(async () => {
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([Rec()], true));
            var stat = new ScriptedStat { Key = "k0" };
            var ticker = new FakeTicker();
            var vm = new ActivityViewModel(reader.Read, stat.Get, ticker);

            vm.OnTabVisibleChanged(true); // immediate read
            await vm.PendingRefreshForTesting!;
            var afterVisible = reader.ReadCalls;

            ticker.Tick();
            ticker.Tick(); // unchanged stat across the 2-tick window -> no re-read
            await vm.PendingRefreshForTesting!;
            var afterUnchanged = reader.ReadCalls;

            stat.Key = "k1";
            ticker.Tick();
            ticker.Tick(); // changed stat, checked on the 2nd tick -> re-read
            await vm.PendingRefreshForTesting!;
            var afterChanged = reader.ReadCalls;

            vm.OnTabVisibleChanged(false);
            stat.Key = "k2";
            ticker.Tick();
            ticker.Tick();
            ticker.Tick();
            ticker.Tick();
            var afterInvisible = reader.ReadCalls;

            return (afterVisible, afterUnchanged, afterChanged, afterInvisible);
        });

        await Assert.That(afterVisible).IsEqualTo(1);
        await Assert.That(afterUnchanged).IsEqualTo(1);
        await Assert.That(afterChanged).IsEqualTo(2);
        await Assert.That(afterInvisible).IsEqualTo(2); // invisible -> polling stopped entirely
    }

    // ---- 5: visibility triggers an immediate refresh ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tab_visible_triggers_immediate_refresh() {
        var (readCallsBefore, readCallsAfter, count) = await AvaloniaSession.DispatchAsync(async () => {
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([Rec()], true));
            var vm = new ActivityViewModel(reader.Read, new ScriptedStat().Get, new FakeTicker());

            var readCallsBefore = reader.ReadCalls;
            vm.OnTabVisibleChanged(true);
            await vm.PendingRefreshForTesting!;

            return (readCallsBefore, reader.ReadCalls, vm.Rows.Count);
        });

        await Assert.That(readCallsBefore).IsEqualTo(0);
        await Assert.That(readCallsAfter).IsEqualTo(1); // exactly one read — guards against an accidental double-read
        await Assert.That(count).IsEqualTo(1);
    }

    // ---- 6: own-resolution refresh is an immediate read, eventual display ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Own_resolution_refresh_is_eventual() {
        var (countAfterVisible, readCallsAfterRequest, countAfterRequest, countAfterTick) = await AvaloniaSession.DispatchAsync(async () => {
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([Rec(agentId: "a1")], true));
            var stat = new ScriptedStat { Key = "k0" };
            var ticker = new FakeTicker();
            var vm = new ActivityViewModel(reader.Read, stat.Get, ticker);

            vm.OnTabVisibleChanged(true);
            await vm.PendingRefreshForTesting!;
            var countAfterVisible = vm.Rows.Count;

            // The ack fires RequestRefresh, but the daemon's own append hasn't landed yet — a stale
            // read is still what an immediate refresh can see.
            vm.RequestRefresh();
            await vm.PendingRefreshForTesting!;
            var readCallsAfterRequest = reader.ReadCalls;
            var countAfterRequest = vm.Rows.Count;

            // The append lands; the next stat-poll tick converges (spec: "no later than the next
            // poll", not "immediately").
            reader.Set(new ConsentLogReadResult([Rec(agentId: "a1"), Rec(agentId: "a2")], true));
            stat.Key = "k1";
            ticker.Tick();
            ticker.Tick();
            await vm.PendingRefreshForTesting!;

            return (countAfterVisible, readCallsAfterRequest, countAfterRequest, vm.Rows.Count);
        });

        await Assert.That(countAfterVisible).IsEqualTo(1);
        await Assert.That(readCallsAfterRequest).IsEqualTo(2);
        await Assert.That(countAfterRequest).IsEqualTo(1);
        await Assert.That(countAfterTick).IsEqualTo(2);
    }

    // ---- 7: a throwing stat or read is swallowed; polling keeps going ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Poll_survives_a_throwing_stat_or_read() {
        var (countAfterVisible, caughtFromThrowingStat, countAfterThrowingStat, countAfterRecoveredTick,
                caughtFromThrowingRead, countAfterThrowingRead, countAfterRecoveredRequest) = await AvaloniaSession.DispatchAsync(async () => {
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([Rec(agentId: "a1")], true));
            var stat = new ScriptedStat { Key = "k0" };
            var ticker = new FakeTicker();
            var vm = new ActivityViewModel(reader.Read, stat.Get, ticker);

            vm.OnTabVisibleChanged(true);
            await vm.PendingRefreshForTesting!;
            var countAfterVisible = vm.Rows.Count;

            stat.ThrowOnce = true;
            Exception? caughtFromThrowingStat = null;
            ticker.Tick();
            ticker.Tick();
            try { await vm.PendingRefreshForTesting!; } catch (Exception ex) { caughtFromThrowingStat = ex; }
            var countAfterThrowingStat = vm.Rows.Count; // unaffected: the read behind it still succeeded

            // A later, healthy tick still drives the poll.
            stat.Key = "k1";
            reader.Set(new ConsentLogReadResult([Rec(agentId: "a1"), Rec(agentId: "a2")], true));
            ticker.Tick();
            ticker.Tick();
            await vm.PendingRefreshForTesting!;
            var countAfterRecoveredTick = vm.Rows.Count;

            reader.ThrowOnce = true;
            Exception? caughtFromThrowingRead = null;
            vm.RequestRefresh();
            try { await vm.PendingRefreshForTesting!; } catch (Exception ex) { caughtFromThrowingRead = ex; }
            var countAfterThrowingRead = vm.Rows.Count; // unaffected by the swallowed throw

            reader.Set(new ConsentLogReadResult([Rec(agentId: "a1")], true));
            vm.RequestRefresh();
            await vm.PendingRefreshForTesting!;

            return (countAfterVisible, caughtFromThrowingStat, countAfterThrowingStat, countAfterRecoveredTick,
                caughtFromThrowingRead, countAfterThrowingRead, vm.Rows.Count);
        });

        await Assert.That(countAfterVisible).IsEqualTo(1);
        await Assert.That(caughtFromThrowingStat).IsNull();
        await Assert.That(countAfterThrowingStat).IsEqualTo(1);
        await Assert.That(countAfterRecoveredTick).IsEqualTo(2);
        await Assert.That(caughtFromThrowingRead).IsNull();
        await Assert.That(countAfterThrowingRead).IsEqualTo(2);
        await Assert.That(countAfterRecoveredRequest).IsEqualTo(1); // recovers on the next call
    }

    // ---- 8: disposal releases the shared ticker ----

    /// The subscription is constructor-scoped (no WhenActivated), and the shared ticker is
    /// Publish().RefCount() — an undisposed subscriber keeps its Interval, and this object,
    /// running past app teardown.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_stops_the_stat_poll() {
        var (readCallsAfterVisible, readCallsAfterDispose, hasObservers) = await AvaloniaSession.DispatchAsync(async () => {
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([Rec()], true));
            var stat = new ScriptedStat { Key = "k0" };
            var ticker = new FakeTicker();
            var vm = new ActivityViewModel(reader.Read, stat.Get, ticker);

            vm.OnTabVisibleChanged(true);
            await vm.PendingRefreshForTesting!;
            var readCallsAfterVisible = reader.ReadCalls;

            vm.Dispose();

            stat.Key = "k1";
            ticker.Tick();
            ticker.Tick();

            return (readCallsAfterVisible, reader.ReadCalls, ticker.Subject.HasObservers);
        });

        await Assert.That(readCallsAfterVisible).IsEqualTo(1);
        await Assert.That(readCallsAfterDispose).IsEqualTo(1);
        await Assert.That(hasObservers).IsFalse();
    }

    // ---- 9: single-flight — a tick during an in-flight read is dropped, not queued ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tick_during_an_in_flight_read_is_dropped_not_queued() {
        var (readCallsWhileBlocked, readCallsAfterRelease, rowsAfterRelease) = await AvaloniaSession.DispatchAsync(async () => {
            var reader = new GatedReader();
            reader.Set(new ConsentLogReadResult([Rec(agentId: "a1")], true));
            var stat = new ScriptedStat { Key = "k0" };
            var ticker = new FakeTicker();
            var vm = new ActivityViewModel(reader.Read, stat.Get, ticker);

            vm.OnTabVisibleChanged(true); // kicks off the read; GatedReader blocks it in flight
            var inFlight = vm.PendingRefreshForTesting!;
            await reader.Started; // the background read is now blocked, provably in flight

            // The stat genuinely changed, so this tick WOULD warrant a fresh read if it ran — the
            // single-flight guard must still drop it because a read is already in flight.
            stat.Key = "k1";
            ticker.Tick();
            ticker.Tick();
            var readCallsWhileBlocked = reader.ReadCalls;

            reader.Release();
            await inFlight;

            return (readCallsWhileBlocked, reader.ReadCalls, vm.Rows.Count);
        });

        await Assert.That(readCallsWhileBlocked).IsEqualTo(1); // the dropped tick never started a 2nd read
        await Assert.That(readCallsAfterRelease).IsEqualTo(1);
        await Assert.That(rowsAfterRelease).IsEqualTo(1);
    }
}
