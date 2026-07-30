using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// Pins the DECIDED behaviour for durable state that can outlive a Cursor session: a
/// well-formed marker without an ack fails CLOSED (and silently), while a malformed marker
/// fails OPEN to top-level. The asymmetry is deliberate and is only defensible because the
/// offline recovery path (<c>kcap import --cursor</c> plus the server-side adoption sweep)
/// exists.
///
/// <para>
/// None of these states has a producer on the measured cursor-agent contract — the arm that
/// would write a marker never runs there — but consumption is NOT gated: TryLoadLink runs on
/// every event, so a marker persisted by another surface or an older build is still read.
/// See docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md (D2a).
/// </para>
/// <para>
/// [NotInParallel] — and deliberately UNKEYED, matching CursorWatcherSpawnTests. The marker
/// paths here are per-test GUIDs and would be safe on their own, but the tests that drive the
/// dispatcher install <see cref="WatcherManager.SpawnOverrideForTesting"/>, which is
/// process-wide and is also mutated from other classes. Observed, not assumed: without this the
/// two known-risk tests pass individually and fail when the class runs together.
/// </para>
/// </summary>
[NotInParallel]
public class CursorSubagentStaleStateTests {
    static string MarkerPath(string child) =>
        Path.Combine(PathHelpers.ConfigPath("cursor-subagent-links"), child);

    static string NewSessionId() => Guid.NewGuid().ToString("N");

    // ---------------------------------------------------------------------------------
    // Contract pins. Each must fail when the behaviour it guards is removed.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task A_well_formed_marker_is_loaded_and_would_activate_the_divert() {
        var child = NewSessionId();
        try {
            CursorLiveSubagentLinker.SaveLink(child, "parent-sid", "researcher");

            var marker = CursorLiveSubagentLinker.TryLoadLink(child);
            await Assert.That(marker).IsNotNull();
            await Assert.That(marker!.Value.ParentSessionId).IsEqualTo("parent-sid");
            await Assert.That(marker.Value.SubagentType).IsEqualTo("researcher");
        } finally { TryDeleteMarker(child); }
    }

    [Test]
    public async Task A_truncated_marker_fails_open_to_top_level() {
        var child = NewSessionId();
        try {
            Directory.CreateDirectory(PathHelpers.ConfigPath("cursor-subagent-links"));
            // One line only: TryLoadLink requires >= 2 with a non-empty first.
            File.WriteAllText(MarkerPath(child), "only-one-line\n");

            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally { TryDeleteMarker(child); }
    }

    [Test]
    public async Task A_marker_with_an_empty_parent_id_also_fails_open() {
        var child = NewSessionId();
        try {
            Directory.CreateDirectory(PathHelpers.ConfigPath("cursor-subagent-links"));
            File.WriteAllText(MarkerPath(child), "\ntask\n");

            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally { TryDeleteMarker(child); }
    }

    /// <summary>
    /// The fail-closed half of the asymmetry, driven through the real dispatcher: a marker
    /// activates the divert, but with no ack every non-start hook must return at the
    /// HasSubagentStartAck gate — suppressing BOTH the raw event and the transcript backfill.
    /// Deleting that gate makes the transcript POST reappear and fails this test.
    /// </summary>
    [Test]
    public async Task A_marker_without_an_ack_suppresses_the_raw_event_and_the_transcript_backfill() {
        using var tmp = new TempDir();
        var child  = NewSessionId();
        var parent = NewSessionId();
        var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
        await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            CursorLiveSubagentLinker.SaveLink(child, parent, "task");
            // Deliberately NO MarkSubagentStartAcked.

            var routes = new List<string>();
            using var handler = new StubHandler((req, _) => {
                routes.Add(req.RequestUri!.AbsolutePath);
                return req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));

            await CursorHookCommand.HandleCore(
                client, "http://s",
                new StringReader($$"""{"hook_event_name":"afterAgentThought","session_id":"{{child}}","generation_id":"g","text":"t","transcript_path":"{{childFile.Replace(@"\", @"\\")}}"}"""),
                spool, TimeSpan.FromSeconds(5));

            // Raw event suppressed...
            await Assert.That(routes).DoesNotContain("/hooks/agent-thought/cursor");
            // ...and so is the agent-routed transcript backfill. This is the assertion the gate
            // owns: without it the backfill runs even though SubagentStarted was never appended.
            await Assert.That(routes.Any(r => r.StartsWith("/hooks/transcript"))).IsFalse();
            await Assert.That(spawned).IsEmpty();

            // Fail-closed AND SILENT: nothing is logged, surfaced or marked at the moment of the
            // loss. Recovery is `kcap import --cursor` plus the adoption sweep.
            await Assert.That(CursorMarkers.HasSubagentStartAck(child)).IsFalse();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            TryDeleteMarker(child);
        }
    }

    // ---------------------------------------------------------------------------------
    // KNOWN-BUG CHARACTERIZATION TESTS — NOT a contract.
    //
    // SaveLink swallows a write failure, and the caller has ALREADY assigned subagentParentId
    // before calling it, so the divert still runs. Both tests below drive the divert with the
    // marker write blocked, which is what the caller does in that state, and record the
    // resulting corrupt state: side effects land with NO marker on disk, so later invocations
    // miss TryLoadLink and route the same child top-level as well.
    //
    // The design spec (D2a) labels these states UNSUPPORTED and lists remedies; the leading one
    // is to have SaveLink report success and fail open BEFORE the start is posted. These two are
    // therefore EXCLUDED from the mutation rule that governs the pins above. When a remedy lands
    // they are EXPECTED to fail — rewrite or delete them then. Do not "fix" them to keep passing.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task Successful_start_with_a_failed_marker_write_leaves_ack_and_watcher_without_a_marker_known_risk() {
        using var tmp = new TempDir();
        var child  = NewSessionId();
        var parent = NewSessionId();
        var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
        await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        var blocker = MarkerPath(child);
        try {
            // A DIRECTORY where the marker FILE must go: File.WriteAllLines throws and SaveLink
            // swallows it. (Chosen over redirecting KCAP_CONFIG_DIR, which PathHelpers resolves
            // into a process-wide static readonly field.)
            Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
            Directory.CreateDirectory(blocker);
            CursorLiveSubagentLinker.SaveLink(child, parent, "task");
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();

            using var handler = new StubHandler((req, _) =>
                req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));

            // The caller proceeds regardless — it already holds parent/child in memory.
            await CursorHookCommand.HandleSubagentChildEventAsync(
                client, "http://s", spool, child, "sessionStart", childFile, parent, "task",
                budgetExpired: () => false, CancellationToken.None);

            // THE FINDING: ack + child watcher exist, with no marker to tie them to. Any later
            // invocation misses TryLoadLink and routes this child as its own top-level session,
            // while this watcher keeps feeding it under the parent — dual routing.
            await Assert.That(CursorMarkers.HasSubagentStartAck(child)).IsTrue();
            await Assert.That(spawned).IsEquivalentTo([$"{parent}-{child}"]);
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            try { Directory.Delete(blocker, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Spooled_start_with_a_failed_marker_write_dual_routes_on_the_next_hook_known_risk() {
        using var tmp = new TempDir();
        var child  = NewSessionId();
        var parent = NewSessionId();
        var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
        await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        var blocker = MarkerPath(child);
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
            Directory.CreateDirectory(blocker);
            CursorLiveSubagentLinker.SaveLink(child, parent, "task");
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();

            var startAttempts = 0;
            var routes = new List<string>();
            using var handler = new StubHandler((req, _) => {
                routes.Add(req.RequestUri!.AbsolutePath);
                if (req.RequestUri!.AbsolutePath == "/hooks/subagent-start") {
                    startAttempts++;
                    return startAttempts == 1
                        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) // spooled
                        : new HttpResponseMessage(HttpStatusCode.OK);                // drained
                }
                return req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));

            // First: the start POST fails and is spooled — with no marker on disk.
            await CursorHookCommand.HandleSubagentChildEventAsync(
                client, "http://s", spool, child, "sessionStart", childFile, parent, "task",
                budgetExpired: () => false, CancellationToken.None);
            await Assert.That(spool.HasBacklog(child)).IsTrue();
            await Assert.That(spawned).IsEmpty();

            // Next hook for the same child. THE FINDING: the drain delivers the spooled start and
            // spawns the {parent}-{child} watcher, while the hook itself — having missed
            // TryLoadLink — takes the ordinary top-level route. The same child ends up ingested
            // BOTH under the parent and as its own session.
            routes.Clear();
            await CursorHookCommand.HandleCore(
                client, "http://s",
                new StringReader($$"""{"hook_event_name":"afterAgentResponse","session_id":"{{child}}","transcript_path":"{{childFile.Replace(@"\", @"\\")}}"}"""),
                spool, TimeSpan.FromSeconds(5));

            // TWO watchers now tail the SAME transcript: one agent-scoped under the parent
            // (spawned by the drain from the spooled payload) and one keyed on the bare child id
            // (spawned by the ordinary top-level path, because TryLoadLink missed). That pair is
            // the dual-routing finding in its most direct form.
            await Assert.That(spawned).Contains($"{parent}-{child}");   // under the parent
            await Assert.That(spawned).Contains(child);                 // ...and as its own session
            await Assert.That(routes).Contains("/hooks/agent-response/cursor"); // top-level route too
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            try { Directory.Delete(blocker, true); } catch { /* best effort */ }
        }
    }

    static void TryDeleteMarker(string child) {
        try { File.Delete(MarkerPath(child)); } catch { /* best effort */ }
    }

    sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> impl) : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return impl(request, body);
        }
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-cursor-stale-state-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
