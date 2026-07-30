using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// Task 9 — the precedence-ordered per-session Cursor watcher spawn.
/// <see cref="CursorHookCommand.ShouldSpawnWatcher"/> is pure (no I/O); the
/// <see cref="CursorHookCommand.MaybeSpawnWatcherAsync"/> tests use
/// <see cref="WatcherManager.SpawnOverrideForTesting"/> so no real OS process is ever spawned.
/// [NotInParallel] because the override and KCAP_CONFIG_DIR are process-wide. Tests in other
/// classes also mutate those values, so a class-specific constraint key is not sufficient.
/// </summary>
[NotInParallel]
public class CursorWatcherSpawnTests {
    static string NewSessionId() => Guid.NewGuid().ToString("N");

    [Test]
    public async Task SessionEnd_never_spawns() =>
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionEnd", isSubagentChild: false)).IsFalse();

    [Test]
    public async Task SessionEnd_never_spawns_even_for_a_correlated_child() =>
        // Precedence ①: terminal beats everything, including a would-be child spawn.
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionEnd", isSubagentChild: true)).IsFalse();

    [Test]
    public async Task Correlated_child_never_spawns_toplevel() =>
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionStart", isSubagentChild: true)).IsFalse();

    [Test]
    public async Task NonTerminal_toplevel_spawns() {
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionStart", isSubagentChild: false)).IsTrue();
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("afterAgentResponse", isSubagentChild: false)).IsTrue();
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("beforeSubmitPrompt", isSubagentChild: false)).IsTrue();
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("postToolUse", isSubagentChild: false)).IsTrue();
    }

    [Test]
    public async Task Spawn_is_suppressed_when_session_is_quarantined() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        CursorMarkers.Quarantine(sid, "test");
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await CursorHookCommand.MaybeSpawnWatcherAsync("http://s", sid, "/tmp/qsid.jsonl", cwd: null, eventName: "sessionStart", isSubagentChild: false);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task Spawn_is_suppressed_for_sessionEnd() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await CursorHookCommand.MaybeSpawnWatcherAsync("http://s", sid, "/tmp/x.jsonl", cwd: null, eventName: "sessionEnd", isSubagentChild: false);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task Spawn_is_suppressed_for_a_correlated_child() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await CursorHookCommand.MaybeSpawnWatcherAsync("http://s", sid, "/tmp/x.jsonl", cwd: null, eventName: "sessionStart", isSubagentChild: true);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task Spawn_is_suppressed_when_transcript_path_is_empty() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await CursorHookCommand.MaybeSpawnWatcherAsync("http://s", sid, "", cwd: null, eventName: "sessionStart", isSubagentChild: false);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task NonQuarantined_toplevel_spawn_invokes_the_watcher_manager_keyed_on_the_session_id() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await CursorHookCommand.MaybeSpawnWatcherAsync("http://s", sid, "/tmp/x.jsonl", cwd: null, eventName: "sessionStart", isSubagentChild: false);
            await Assert.That(spawned).IsEquivalentTo([sid]);
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    // Task 12 — the child (subagent) watcher must never be spawned before the server
    // has acknowledged the diverted subagent-start (2xx). A spooled start (POST failure) defers
    // the spawn entirely; a later invocation whose spool drain finally delivers the start is
    // what performs it. Invariant under test: no child transcript line — and here, no child
    // watcher at all — exists before SubagentStarted lands.
    [Test]
    public async Task Child_watcher_not_spawned_when_subagent_start_spooled() {
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", tmp.Path);
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            // subagent-start POST fails (503 → spooled). A growing child transcript file exists.
            using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            using var client   = new HttpClient(handler);
            var child  = NewSessionId();
            var parent = NewSessionId();
            var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));
            await CursorHookCommand.HandleSubagentChildEventAsync(
                client, "http://s", spool, child, "sessionStart", childFile, parent, "task",
                budgetExpired: () => false, ct: CancellationToken.None);

            await Assert.That(spawned).IsEmpty(); // start not acked → no child watcher
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        }
    }

    [Test]
    public async Task Child_watcher_spawned_with_parent_child_key_once_subagent_start_is_acked() {
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", tmp.Path);
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client   = new HttpClient(handler);
            var child  = NewSessionId();
            var parent = NewSessionId();
            var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));
            await CursorHookCommand.HandleSubagentChildEventAsync(
                client, "http://s", spool, child, "sessionStart", childFile, parent, "task",
                budgetExpired: () => false, ct: CancellationToken.None);

            await Assert.That(spawned).IsEquivalentTo([$"{parent}-{child}"]);
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        }
    }

    [Test]
    public async Task Child_watcher_not_spawned_when_the_parent_session_is_quarantined() {
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", tmp.Path);
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client   = new HttpClient(handler);
            var child  = NewSessionId();
            var parent = NewSessionId();
            var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");
            CursorMarkers.Quarantine(parent, "test");

            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));
            await CursorHookCommand.HandleSubagentChildEventAsync(
                client, "http://s", spool, child, "sessionStart", childFile, parent, "task",
                budgetExpired: () => false, ct: CancellationToken.None);

            await Assert.That(spawned).IsEmpty();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        }
    }

    // Task 12 — the deferred half of the acked-start gate: a subagent-start that failed
    // its first live POST (spooled by HandleSubagentChildEventAsync) must still spawn the child
    // watcher once a LATER hook invocation's generic spool drain (HandleCore, top of method —
    // runs before the isSubagentChild divert) finally delivers it. Exercises the real dispatcher
    // end to end rather than calling HandleSubagentChildEventAsync directly.
    [Test]
    public async Task Deferred_spool_drain_delivering_a_spooled_subagent_start_spawns_the_child_watcher() {
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", tmp.Path);
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            var child  = NewSessionId();
            var parent = NewSessionId();
            var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

            // Pre-link the child to its parent — mirrors CursorHookCommand.SaveLink at the
            // child's own sessionStart, so the second invocation's TryLoadLink divert kicks in
            // without re-running the correlator.
            CursorLiveSubagentLinker.SaveLink(child, parent, "task");

            using var handler = new StubHandler((req, _) =>
                req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));

            // Seed the undelivered subagent-start DIRECTLY (as a prior transient POST failure
            // would have left it), rather than producing one by driving the child's own
            // sessionStart. A real Cursor subagent child never fires sessionStart, so using it
            // as the vehicle would bake in a trigger that cannot occur — see
            // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
            spool.Append(child, "subagent-start",
                $$"""{"hook_event_name":"subagent_start","session_id":"{{parent}}","agent_id":"{{child}}","transcript_path":"{{childFile.Replace(@"\", @"\\")}}"}""");

            await Assert.That(spawned).IsEmpty(); // not yet acked

            // Second invocation: any later hook for the same child. HandleCore's generic
            // top-of-method spool drain redelivers the spooled subagent-start FIRST (before the
            // isSubagentChild divert even runs) — this time it succeeds, and that success is
            // what must trigger the deferred spawn.
            await CursorHookCommand.HandleCore(
                client, "http://s",
                new StringReader($$"""{"hook_event_name":"postToolUse","session_id":"{{child}}","tool_name":"Bash"}"""),
                spool, TimeSpan.FromSeconds(2));

            await Assert.That(spawned).IsEquivalentTo([$"{parent}-{child}"]);
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        }
    }

    // a subagent-start that hits a non-transient 4xx on retry (via
    // HandleCore's generic top-of-method spool drain) is permanently DROPPED — HookSpool removes
    // the entry, so HasBacklog goes false even though no AgentSubsession stream was ever opened
    // server-side. Before the fix, that emptied backlog let the child's own content-less hooks
    // (and its own subagent-stop) run the agent-routed transcript backfill unconditionally. The
    // fix gates on the durable ack marker instead of "no backlog", so a dropped start must
    // permanently block ALL child transcript delivery — not just the watcher spawn.
    [Test]
    public async Task Permanently_dropped_subagent_start_gates_all_child_transcript_delivery_forever() {
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", tmp.Path);
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            var child     = NewSessionId();
            var parent    = NewSessionId();
            var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

            // Pre-link the child to its parent so every hook for `child` diverts through
            // HandleSubagentChildEventAsync without needing the correlator to re-run.
            CursorLiveSubagentLinker.SaveLink(child, parent, "task");

            var transcriptPosts = 0;
            using var handler = new StubHandler((req, _) => {
                // Every subagent-start attempt 400s: a non-transient 4xx, which HookSpool treats
                // as a permanent Drop (the entry is discarded, not re-queued).
                if (req.RequestUri!.AbsolutePath == "/hooks/subagent-start") {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
                if (req.RequestUri!.AbsolutePath == "/hooks/transcript") {
                    transcriptPosts++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));

            var childFileEscaped = childFile.Replace(@"\", @"\\");

            // Seed the undelivered subagent-start DIRECTLY, as a prior transient POST failure
            // would have left it. Driving the child's own sessionStart to produce it would bake
            // in a trigger a real Cursor child never fires — see
            // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
            spool.Append(child, "subagent-start",
                $$"""{"hook_event_name":"subagent_start","session_id":"{{parent}}","agent_id":"{{child}}","transcript_path":"{{childFileEscaped}}"}""");

            await Assert.That(spawned).IsEmpty();

            // 2nd invocation: any later hook for this child. HandleCore's generic top-of-method
            // spool drain retries the spooled subagent-start FIRST — this time it 400s, which
            // HookSpool treats as a permanent Drop (the entry is discarded, not re-queued).
            await CursorHookCommand.HandleCore(
                client, "http://s",
                new StringReader($$"""{"hook_event_name":"afterAgentThought","session_id":"{{child}}","generation_id":"g","text":"t","transcript_path":"{{childFileEscaped}}"}"""),
                spool, TimeSpan.FromSeconds(2));

            // 3rd invocation: another content-less hook. Before the fix, the dropped start left
            // HasBacklog false and this would run the agent-routed transcript backfill despite
            // SubagentStarted never having been appended.
            await CursorHookCommand.HandleCore(
                client, "http://s",
                new StringReader($$"""{"hook_event_name":"postToolUse","session_id":"{{child}}","tool_name":"Bash","transcript_path":"{{childFileEscaped}}"}"""),
                spool, TimeSpan.FromSeconds(2));

            await Assert.That(spawned).IsEmpty();      // never acked -> no child watcher ever
            await Assert.That(transcriptPosts).IsEqualTo(0); // never acked -> no child transcript ever
            await Assert.That(CursorMarkers.HasSubagentStartAck(child)).IsFalse();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        }
    }

    // once subagent-start is acked, every LATER NONTERMINAL hook for the
    // same child must attempt to (re)spawn its watcher — not just the child's own sessionStart.
    // Before the fix, only sessionStart ever called MaybeSpawnChildWatcherAsync, so a child
    // watcher that later exited (the newly-enabled idle ceiling), crashed, or never actually
    // started (e.g. its acked sessionStart carried no transcript path) was never restarted.
    [Test]
    public async Task Later_nonterminal_child_hook_self_heals_a_dead_or_never_started_child_watcher_via_the_ack_marker() {
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", tmp.Path);
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            var child     = NewSessionId();
            var parent    = NewSessionId();
            var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

            // Subagent-start was acked in an EARLIER process invocation (durable marker) — the
            // watcher spawned then may since have died; this is a LATER, separate hook call.
            CursorMarkers.MarkSubagentStartAcked(child);

            using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client  = new HttpClient(handler);
            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));

            await CursorHookCommand.HandleSubagentChildEventAsync(
                client, "http://s", spool, child, "postToolUse", childFile, parent, "task",
                budgetExpired: () => false, ct: CancellationToken.None);

            await Assert.That(spawned).IsEquivalentTo([$"{parent}-{child}"]);
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        }
    }

    // Retain the terminal no-spawn rule: sessionEnd must never trigger a self-heal spawn, even
    // once the ack marker exists — subagent-stop is the child's last hook, and spawning a watcher
    // moments before the session ends would be pure churn (mirrors ShouldSpawnWatcher's ①).
    [Test]
    public async Task Terminal_sessionEnd_child_hook_still_never_spawns_even_when_acked() {
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", tmp.Path);
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            var child     = NewSessionId();
            var parent    = NewSessionId();
            var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");
            CursorMarkers.MarkSubagentStartAcked(child);

            using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client  = new HttpClient(handler);
            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));

            await CursorHookCommand.HandleSubagentChildEventAsync(
                client, "http://s", spool, child, "sessionEnd", childFile, parent, "task",
                budgetExpired: () => false, ct: CancellationToken.None);

            await Assert.That(spawned).IsEmpty();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        }
    }

    // A later nonterminal hook for a child WITHOUT the ack marker must still self-heal nothing —
    // the existing no-ack gate (review fix #5's round-1 sibling) already returns before this
    // point, so the self-heal spawn never even gets a chance to run.
    [Test]
    public async Task Later_nonterminal_child_hook_does_not_spawn_when_never_acked() {
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", tmp.Path);
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            var child     = NewSessionId();
            var parent    = NewSessionId();
            var childFile = Path.Combine(tmp.Path, $"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");
            // No CursorMarkers.MarkSubagentStartAcked call — never acked.

            using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client  = new HttpClient(handler);
            var spool = new HookSpool(Path.Combine(tmp.Path, "spool"));

            await CursorHookCommand.HandleSubagentChildEventAsync(
                client, "http://s", spool, child, "postToolUse", childFile, parent, "task",
                budgetExpired: () => false, ct: CancellationToken.None);

            await Assert.That(spawned).IsEmpty();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        }
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
            $"kcap-cursor-child-watcher-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
