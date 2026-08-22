using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

// Several tests here read HOME-derived paths (DisabledSessions marker dir,
// PathHelpers.HomeDirectory injection into the outgoing payload) and one
// mutates KCAP_AGENT_ID. Serialise against every other test that
// mutates HOME so a racing HOME-setter from PluginCommand* tests can't
// land our marker writes in the wrong directory.
// TokenStoreProfileTests too: these read the shared config.json, and a concurrent writer there
// DELETES it to republish — which a plain read handle blocks on Windows.
[NotInParallel(["HomeEnvVarMutation", "TokenStoreProfileTests"])]
public class CursorHookCommandTests {
    const string Sid = "8c3276c2c8f743ce98898c2becf5240a";

    [Test]
    public async Task malformed_stdin_returns_zero() {
        using var fx   = new Fixture();
        var       exit = await fx.HandleAsync("not a json payload");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.Sent).IsEmpty();
    }

    [Test]
    public async Task missing_hook_event_name_returns_zero() {
        using var fx   = new Fixture();
        var       exit = await fx.HandleAsync("""{"session_id":"abc"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.Sent).IsEmpty();
    }

    [Test]
    public async Task session_id_is_normalised_dashless_in_outgoing_payload() {
        using var fx = new Fixture();
        await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"8c3276c2-c8f7-43ce-9889-8c2becf5240a"}""");
        var sent = fx.SentToHook("session-start/cursor");

        await Assert.That(JsonNode.Parse(sent)!["session_id"]!.GetValue<string>())
            .IsEqualTo("8c3276c2c8f743ce98898c2becf5240a");
    }

    [Test]
    [NotInParallel("CapacitorAgentIdEnvVar")]
    public async Task home_dir_and_agent_host_id_are_injected() {
        Environment.SetEnvironmentVariable("KCAP_AGENT_ID", "host-42");

        try {
            using var fx = new Fixture();
            await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc"}""");
            var sent = fx.SentToHook("session-start/cursor");
            var node = JsonNode.Parse(sent)!;
            await Assert.That(node["home_dir"]?.GetValue<string>()).IsNotNull();
            await Assert.That(node["agent_host_id"]?.GetValue<string>()).IsEqualTo("host-42");
        } finally {
            Environment.SetEnvironmentVariable("KCAP_AGENT_ID", null);
        }
    }

    [Test]
    public async Task disabled_session_suppresses_POST() {
        var sid = Guid.NewGuid().ToString("N");
        DisabledSessions.Mark(sid);

        try {
            using var fx = new Fixture();
            await fx.HandleAsync($$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""");
            await Assert.That(fx.Sent).IsEmpty();
        } finally {
            DisabledSessions.RemoveMarker(sid);
        }
    }

    [Test]
    public async Task telemetry_events_post_but_do_not_spool_on_failure() {
        using var fx = new Fixture(postStatus: HttpStatusCode.InternalServerError);
        await fx.HandleAsync("""{"hook_event_name":"preToolUse","session_id":"abc","tool_name":"Glob"}""");
        await Assert.That(fx.SpoolFiles).IsEmpty();
    }

    /// <summary>
    /// Cursor POSTs directly rather than through <c>AgentHookPoster</c>, so it needs its own
    /// rejected-credential nudge — without it Cursor is the one vendor whose users get no
    /// explanation and no <c>kcap login</c> hint. Redirects the process-global Console.Error, so
    /// it runs alone.
    /// </summary>
    [Test, NotInParallel]
    public async Task server_rejected_credential_names_kcap_login_on_stderr() {
        using var fx = new Fixture(postStatus: HttpStatusCode.Unauthorized);
        using var capture = ConsoleOutput.StartErrorCapture("\n");

        await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");

        await Assert.That(capture.GetCapturedError()).Contains("kcap login");
    }

    /// <summary>Non-vacuous control: a non-401 failure keeps the bare status line, so the test
    /// above is proving 401 recognition rather than that any failure mentions the command.</summary>
    [Test, NotInParallel]
    public async Task server_error_does_not_name_kcap_login_on_stderr() {
        using var fx = new Fixture(postStatus: HttpStatusCode.InternalServerError);
        using var capture = ConsoleOutput.StartErrorCapture("\n");

        await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");

        await Assert.That(capture.GetCapturedError()).DoesNotContain("kcap login");
    }

    [Test]
    public async Task canonical_events_spool_on_POST_failure() {
        using var fx = new Fixture(postStatus: HttpStatusCode.InternalServerError);
        await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0]).EndsWith(Sid + ".jsonl");
    }

    [Test]
    public async Task spool_drain_runs_before_current_event_under_budget() {
        using var fx = new Fixture();
        fx.Spool.Append(Sid, "session-start/cursor", $$"""{"hook_event_name":"sessionStart","session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");
        await Assert.That(fx.RouteOrder).IsEquivalentTo(["session-start/cursor", "session-end/cursor"]);
    }

    // a telemetry-only mapping (postToolUse, SpoolOnFailure=false) must
    // NOT let the recovery-spawn watcher start while an EARLIER queued canonical event (here:
    // sessionStart) is still stuck undelivered. Simulate: sessionStart is already spooled from a
    // prior failed invocation; THIS invocation's generic top-of-method drain retries it and hits
    // a TRANSIENT failure (503) so it stays queued, while postToolUse's OWN POST succeeds. Before
    // the fix, postToolUse's SpoolOnFailure=false meant the ordering guard never even looked at
    // the backlog, and the recovery spawn ran regardless.
    [Test]
    public async Task telemetry_hook_does_not_recovery_spawn_while_an_earlier_canonical_event_is_still_stuck() {
        var sid = Guid.NewGuid().ToString("N");
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", tmp.Path);

        var spool = new HookSpool(tmp.PathTo("spool"));
        spool.Append(sid, "session-start/cursor", $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""");

        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

        try {
            using var handler = new StubHandler(req => {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/hooks/session-start/cursor") {
                    // Transient failure on retry — the entry stays queued (NOT delivered, NOT dropped).
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }
                if (req.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); // postToolUse's own POST succeeds
            });
            using var client = new HttpClient(handler);

            var exit = await CursorHookCommand.HandleCore(
                client, "http://localhost",
                new StringReader($$"""{"hook_event_name":"postToolUse","session_id":"{{sid}}","tool_name":"Bash","transcript_path":"/tmp/{{sid}}.jsonl"}"""),
                spool, TimeSpan.FromSeconds(2));

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(spawned).IsEmpty(); // must NOT spawn while sessionStart is still stuck
            await Assert.That(spool.HasBacklog(sid)).IsTrue(); // confirms the premise: still queued, not delivered
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        }
    }

    [Test]
    public async Task afterAgentThought_canonical_id_is_stable_across_replays() {
        using var fx   = new Fixture();
        var       body = """{"hook_event_name":"afterAgentThought","session_id":"abc","generation_id":"gen1","text":"hello"}""";
        await fx.HandleAsync(body);
        await fx.HandleAsync(body);

        var ids = fx.AllSentTo("agent-thought/cursor")
            .Select(b => JsonNode.Parse(b)!["canonical_event_id"]!.GetValue<string>())
            .Distinct()
            .ToList();
        await Assert.That(ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task sessionEnd_drains_transcript_before_posting_terminal_hook() {
        // Server's HandleSessionEnd clears the CursorAttachmentsFifo as soon
        // as it accepts the /hooks/session-end/cursor POST. If the CLI posted
        // sessionEnd first and only then ran the transcript backfill, the
        // final user line in the transcript would be normalized AFTER the
        // FIFO was wiped and any queued beforeSubmitPrompt attachments would
        // be lost. Verify the order is: transcript batch → session-end.
        using var fx = new Fixture();

        await fx.WriteTranscript(
            """{"role":"user","message":{"content":[{"type":"text","text":"final prompt"}]}}"""
        );

        await fx.HandleAsync(
            $$"""
               {"hook_event_name":"sessionEnd","session_id":"{{Sid}}","transcript_path":"{{fx.TranscriptPathEscaped}}"}
               """
        );

        var transcriptIdx = fx.RouteOrder.FindIndex(r => r == "transcript");
        var sessionEndIdx = fx.RouteOrder.FindIndex(r => r == "session-end/cursor");

        await Assert.That(transcriptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(sessionEndIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(transcriptIdx).IsLessThan(sessionEndIdx);
    }

    [Test]
    public async Task non_sessionEnd_events_still_post_before_backfill() {
        // Regression guard: only sessionEnd swaps the order. Other events
        // (here: beforeSubmitPrompt) must keep the existing post-then-backfill
        // ordering so lifecycle metadata reaches the server before any new
        // transcript context.
        using var fx = new Fixture();

        await fx.WriteTranscript(
            """{"role":"user","message":{"content":[{"type":"text","text":"hello"}]}}"""
        );

        await fx.HandleAsync(
            $$"""
               {"hook_event_name":"beforeSubmitPrompt","session_id":"{{Sid}}","prompt":"hello","transcript_path":"{{fx.TranscriptPathEscaped}}"}
               """
        );

        var transcriptIdx = fx.RouteOrder.FindIndex(r => r == "transcript");
        var promptIdx     = fx.RouteOrder.FindIndex(r => r == "user-prompt/cursor");

        await Assert.That(promptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(transcriptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(promptIdx).IsLessThan(transcriptIdx);
    }

    [Test]
    public async Task telemetry_only_hook_touches_the_heartbeat_file() {
        // Task 8: even a telemetry-only hook (never spooled, lossy on failure) must
        // touch the per-session heartbeat — it reflects "Cursor is still firing hooks",
        // independent of whatever the transcript/spool machinery is doing.
        using var fx = new Fixture();
        var       sid = Guid.NewGuid().ToString("N");
        var       before = DateTimeOffset.UtcNow;

        await fx.HandleAsync($$"""{"hook_event_name":"postToolUse","session_id":"{{sid}}","tool_name":"Bash"}""");

        var heartbeat = WatcherHeartbeat.Read(CursorMarkers.HeartbeatPath(sid));
        await Assert.That(heartbeat).IsNotNull();
        await Assert.That(heartbeat!.Value).IsGreaterThanOrEqualTo(before);
    }

    [Test]
    public async Task beforeSubmitPrompt_clears_its_barrier_once_the_live_POST_succeeds() {
        using var fx  = new Fixture(); // defaults to HttpStatusCode.OK
        var       sid = Guid.NewGuid().ToString("N");

        await fx.HandleAsync($$"""{"hook_event_name":"beforeSubmitPrompt","session_id":"{{sid}}","prompt":"hi"}""");

        await Assert.That(CursorMarkers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task beforeSubmitPrompt_barrier_stays_pending_when_the_live_POST_fails() {
        using var fx  = new Fixture(postStatus: HttpStatusCode.InternalServerError);
        var       sid = Guid.NewGuid().ToString("N");

        await fx.HandleAsync($$"""{"hook_event_name":"beforeSubmitPrompt","session_id":"{{sid}}","prompt":"hi"}""");

        await Assert.That(CursorMarkers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsTrue();
    }

    [Test]
    public async Task sessionEnd_drains_the_hook_spool_before_the_pre_end_transcript_drain_and_clears_the_barrier() {
        // Task 8: a beforeSubmitPrompt whose live POST previously failed left a barrier
        // + a spooled user-prompt/cursor entry behind. sessionEnd must deliver that spooled
        // entry (clearing the barrier) BEFORE running its pre-end transcript drain, so a
        // transcript line depending on the attachment is never normalized ahead of it.
        using var fx  = new Fixture();
        var       sid = Guid.NewGuid().ToString("N");

        CursorMarkers.CreateBarrier(sid, DateTimeOffset.UtcNow);
        fx.Spool.Append(sid, "user-prompt/cursor", $$"""{"hook_event_name":"beforeSubmitPrompt","session_id":"{{sid}}"}""");

        await fx.WriteTranscript(
            """{"role":"user","message":{"content":[{"type":"text","text":"final prompt"}]}}"""
        );

        await fx.HandleAsync(
            $$"""
               {"hook_event_name":"sessionEnd","session_id":"{{sid}}","transcript_path":"{{fx.TranscriptPathEscaped}}"}
               """
        );

        var promptIdx     = fx.RouteOrder.FindIndex(r => r == "user-prompt/cursor");
        var transcriptIdx = fx.RouteOrder.FindIndex(r => r == "transcript");
        var sessionEndIdx = fx.RouteOrder.FindIndex(r => r == "session-end/cursor");

        await Assert.That(promptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(transcriptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(sessionEndIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(promptIdx).IsLessThan(transcriptIdx);
        await Assert.That(transcriptIdx).IsLessThan(sessionEndIdx);

        await Assert.That(CursorMarkers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task null_transcript_path_does_not_trigger_backfill() {
        using var fx = new Fixture();
        await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc","transcript_path":null}""");
        await Assert.That(fx.AllSentTo("transcript")).IsEmpty();
    }

    [Test]
    public async Task expired_budget_returns_zero_not_throws() {
        using var fx = new Fixture();

        // budgetTotal=0 forces BudgetExpired() true on first check, which can also
        // propagate as OperationCanceledException from stdin/HTTP. Either way the
        // dispatcher must fail-open with return 0, never bubble the exception.
        var exit = await CursorHookCommand.HandleCore(
            fx.Client,
            "http://localhost",
            new StringReader("""{"hook_event_name":"sessionStart","session_id":"abc"}"""),
            fx.Spool,
            TimeSpan.Zero
        );
        await Assert.That(exit).IsEqualTo(0);
    }

    [Test]
    public async Task hard_cap_returns_zero_when_inner_ignores_cancellation() {
        // Simulates an uncancellable hang inside TokenStore.RefreshAsync's
        // HttpClient.PostAsync — no CT plumbed through, default 100s timeout.
        // The Task.WhenAny ceiling in CursorHookCommand.Handle must beat that.
        var inner = Task.Run(async () => {
                await Task.Delay(TimeSpan.FromSeconds(10));

                return 42;
            }
        );
        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await CursorHookCommand.WithHardCap(inner, TimeSpan.FromMilliseconds(50));
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task hard_cap_returns_inner_result_when_inner_finishes_first() {
        var inner = Task.FromResult(7);
        var exit  = await CursorHookCommand.WithHardCap(inner, TimeSpan.FromSeconds(2));
        await Assert.That(exit).IsEqualTo(7);
    }

    [Test]
    public async Task fresh_canonical_event_is_spooled_when_drain_consumes_budget() {
        // Drain blocks past the budget by parking the POST handler. The
        // dispatcher must spool the fresh sessionEnd that hasn't been
        // POSTed yet instead of losing it.
        using var fx = new Fixture();
        fx.HoldOnPost = TimeSpan.FromMilliseconds(50);

        fx.Spool.Append(Sid, "session-start/cursor", $$"""{"hook_event_name":"sessionStart","session_id":"{{Sid}}"}""");

        // 30 ms budget — first drained POST eats most of it, BudgetExpired flips
        // before the fresh event can post. The fresh sessionEnd must land back
        // in the spool, replacing the just-delivered sessionStart line.
        //
        // Task 2: HandleCore's outer deadline race (§2) can now return to the
        // caller at the 30ms mark WITHOUT waiting for the still-in-flight drain (holding
        // the fresh sessionEnd's own append until the delayed POST resolves at ~50ms) —
        // mirroring the pre-existing top-level WithHardCap's own "abandon, don't cancel"
        // contract. The append still happens on the abandoned background continuation;
        // poll briefly for it instead of asserting immediately.
        var exit = await CursorHookCommand.HandleCore(
            fx.Client,
            "http://localhost",
            new StringReader($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}"""),
            fx.Spool,
            TimeSpan.FromMilliseconds(30)
        );

        await Assert.That(exit).IsEqualTo(0);

        // Poll for CONTENT, not existence: the spool file is seeded → deleted once drained → re-created by
        // the fresh sessionEnd, and an existence check cannot tell the seeded file from the re-created one.
        // The handle must block nothing the drain does — HookSpool both appends to and File.Move/Delete's
        // this exact path, and every restriction is mandatory on Windows but invisible on Unix.
        // SingleOrDefault: a second *.jsonl for one session id would be a real bug worth throwing on.
        const int budgetSeconds = 10;

        var deadline      = DateTime.UtcNow + TimeSpan.FromSeconds(budgetSeconds);
        var spoolContent  = (string?)null;
        var observedEnd   = false;
        var lastIoFailure = (IOException?)null;

        while (!observedEnd && DateTime.UtcNow < deadline) {
            if (fx.SpoolFiles.SingleOrDefault() is { } path) {
                try {
                    await using var stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    spoolContent  = await reader.ReadToEndAsync();
                    lastIoFailure = null; // a clean read means any earlier failure was the transient race
                    observedEnd   = spoolContent.Contains("sessionEnd", StringComparison.Ordinal);
                } catch (IOException ex) {
                    // The enumerate→open race, not a sharing conflict: the share flags above permit
                    // everything the drain does, but a path EnumerateFiles just returned can be deleted
                    // before we open it.
                    lastIoFailure = ex;
                }
            }

            if (!observedEnd) await Task.Delay(20);
        }

        // Gated on whether the TARGET was observed, not on content being null: a clean read of the stale
        // seeded line leaves content non-null, so a null check would skip this and let a persistent
        // filesystem fault masquerade as "the append never happened".
        if (!observedEnd && lastIoFailure is not null) {
            throw new IOException(
                $"spool never yielded sessionEnd within {budgetSeconds}s; last IO failure: {lastIoFailure.Message}",
                lastIoFailure);
        }

        await Assert.That(spoolContent).IsNotNull();
        await Assert.That(spoolContent!).Contains("sessionEnd");
    }

    // Task 2: single-writer, deadline-safe stdout emission for Cursor's
    // sessionStart. Cursor writes zero stdout for every OTHER event, and (until Task 3
    // wires the real memory fragment) exactly "{}\n" for every resolved sessionStart —
    // whether at the very end of a normal invocation, at an early fail-open return, at the
    // dispatcher deadline (kind published but the inner work hasn't finished), or never
    // (unresolved event / deadline before the event kind is known).

    // Every test below that mutates Console.Out is [NotInParallel] with NO group — i.e.
    // runs strictly alone against the WHOLE suite, not just this class — matching
    // Codex/CodexHookCommandTests' own precedent: a named group only serializes within
    // that group, but other files elsewhere in the suite ALSO mutate the same
    // process-global Console.Out under different (or no) groups, and that cross-group
    // race is what corrupts captures.

    [Test, NotInParallel]
    public async Task SessionStart_emits_empty_object() {
        using var fx = new Fixture();
        using var capture = ConsoleOutput.StartCapture();
        var exit = await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
    }

    [Test, NotInParallel]
    public async Task NonSessionStart_emits_nothing() {
        using var fx = new Fixture();
        using var capture = ConsoleOutput.StartCapture();
        var exit = await fx.HandleAsync("""{"hook_event_name":"postToolUse","session_id":"abc","tool_name":"Bash"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("");
    }

    [Test, NotInParallel]
    public async Task LinkedChild_sessionStart_emits_empty_object() {
        using var fx = new Fixture();
        var parentId = Guid.NewGuid().ToString("N");
        var childId  = Guid.NewGuid().ToString("N");
        // Force the already-linked-child path directly (as an earlier hook would have
        // persisted it) without needing a real sibling transcript to correlate against.
        CursorLiveSubagentLinker.SaveLink(childId, parentId, "task");

        using var capture = ConsoleOutput.StartCapture();
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"sessionStart","session_id":"{{childId}}"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        // A linked child must short-circuit to {} before any orchestrator work.
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
    }

    // Review finding 1: these two guarantees must hold at the level the SINGLE
    // cap+emitter actually lives — CursorHookCommand.HandleInternal, the whole-dispatch entry
    // Handle() itself delegates to (client/auth setup THROUGH the recording+memory dispatch,
    // under exactly one hard-cap deadline). Calling HandleCore directly (as these two tests
    // used to) only proves the dispatch-body race is internally consistent; it can't catch a
    // second, independent cap racing ABOVE it — which is exactly the bug this finding fixed
    // (Handle used to wrap HandleInternal in its own separate WithHardCap(DispatcherBudget),
    // so that outer timer — started before client/auth setup — could win the race against
    // HandleCore's own internal deadline and return with no {} for a resolved sessionStart,
    // while the abandoned HandleCore kept running and could still write late).
    [Test, NotInParallel]
    public async Task HardCap_before_resolve_emits_nothing() {
        using var capture = ConsoleOutput.StartCapture();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var fx = new Fixture();
        // Never resolves within the cap regardless of cancellation — proves the single
        // deadline race genuinely abandons the inner work rather than relying on it
        // noticing. clientFactory/spoolFactory stand in for real auth/spool setup so the
        // test stays hermetic while still exercising the REAL entry point's cap+emit logic.
        var exit = await CursorHookCommand.HandleInternal(
            "http://localhost", new NeverCompletingReader(), TimeSpan.FromMilliseconds(50),
            clientFactory: _ => Task.FromResult((fx.Client, AuthStatus.Ok)),
            spoolFactory: () => fx.Spool);
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("");
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
    }

    // Qodo #2: on the deadline branch, HandleCore must deterministically Cancel() its own
    // `cts` rather than merely disposing it and trusting the CTS's own internal budgetTotal
    // timer to have already fired by coincidence — that internal timer and the Task.Delay
    // deadline task are two independent timers racing the same wall-clock target, so a
    // dispose-without-cancel could leave the abandoned inner's cancellation-aware stdin
    // read/HTTP calls (both bound to cts.Token) never actually observing cancellation. Uses
    // a reader that only ever completes VIA cancellation (never on its own) — mirroring the
    // CancelAwareHandler pattern already used for the memory-fetch cancellation test below —
    // so a prompt, observed cancellation is the only way this test can pass.
    [Test, NotInParallel]
    public async Task HandleCore_deadline_win_cancels_the_abandoned_inners_token() {
        using var capture = ConsoleOutput.StartCapture();
        using var fx = new Fixture();
        var reader = new CancelObservingReader();

        var exit = await CursorHookCommand.HandleCore(
            fx.Client, "http://localhost", reader, fx.Spool, TimeSpan.FromMilliseconds(30));

        await Assert.That(exit).IsEqualTo(0);
        // The read never resolved (no hook_event_name was ever parsed), so there is
        // nothing to emit.
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("");

        // The abandoned reader's ReadToEndAsync only ever completes by observing its
        // CancellationToken fire. Give it a generous window relative to the 30ms budget —
        // this asserts the cancellation was actually delivered promptly by HandleCore's
        // explicit Cancel(), not "eventually, whenever the internal timer happens to tick".
        var won = await Task.WhenAny(reader.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        await Assert.That(won).IsEqualTo(reader.Cancelled.Task);
    }

    [Test, NotInParallel]
    public async Task HardCap_after_resolve_sessionStart_emits_empty_once() {
        using var capture = ConsoleOutput.StartCapture();
        using var fx = new Fixture();
        // sessionStart resolves instantly (fast JSON parse) but the live POST hangs well
        // past the 50ms dispatcher deadline.
        fx.HoldOnPost = TimeSpan.FromMilliseconds(300);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exit = await CursorHookCommand.HandleInternal(
            "http://localhost",
            new StringReader("""{"hook_event_name":"sessionStart","session_id":"abc"}"""),
            TimeSpan.FromMilliseconds(50),
            clientFactory: _ => Task.FromResult((fx.Client, AuthStatus.Ok)),
            spoolFactory: () => fx.Spool);
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));

        // Let the orphaned inner work actually finish in the background, then re-assert
        // stdout is unchanged — the abandoned inner must never get a second/late write.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
    }

    // Review finding 1: the single cap must ALSO cover client/auth setup itself — the
    // one piece that sat OUTSIDE HandleCore's own race pre-fix. A client factory that never
    // completes simulates the documented TokenStore-hang risk (see Handle's doc comment); the
    // single deadline must still fire, return 0, and never let the abandoned auth attempt
    // produce a late write even once it eventually "completes" in the background.
    [Test, NotInParallel]
    public async Task HardCap_during_client_setup_emits_nothing_and_no_late_write() {
        using var capture = ConsoleOutput.StartCapture();
        using var fx = new Fixture();
        var neverAuths = new TaskCompletionSource<(HttpClient, AuthStatus)>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exit = await CursorHookCommand.HandleInternal(
            "http://localhost",
            new StringReader("""{"hook_event_name":"sessionStart","session_id":"abc"}"""),
            TimeSpan.FromMilliseconds(50),
            clientFactory: _ => neverAuths.Task,
            spoolFactory: () => fx.Spool);
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("");
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));

        // Even if the abandoned auth call eventually resolves in the background, HandleCore
        // is never invoked for this attempt — there must be no late write.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("");
    }

    // Task 3: the shared memory orchestrator wired in for a top-level (non-child)
    // sessionStart — fragment, lifecycle, budget, opt-out, and workspace-root behavior.

    [Test, NotInParallel]
    public async Task Ready_fragment_emitted() {
        using var fx = new Fixture();
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";
        var sid = Guid.NewGuid().ToString("N");

        using var capture = ConsoleOutput.StartCapture();
        // A generous budget (well beyond the production 2s) — recording-critical work here
        // is a handful of in-memory/fake-HTTP steps that normally finish in well under a
        // millisecond, but memBudget = budgetTotal - elapsed - HookBudget.Safety(1.5s) leaves
        // only ~0.5s of margin at the production 2s default; under heavy CI/full-suite CPU
        // contention that margin can occasionally be exhausted by scheduling delays alone,
        // which would make this assert on the WRONG thing (a legitimate no-budget skip, not
        // a bug). This test is about the fragment/lifecycle wiring, not the budget math
        // (NoBudget_skips_provider owns that), so give it comfortable headroom.
        // A real non-repo workspace root (system temp) is required now that an absent root
        // skips injection; forward-slashed so it's valid JSON on Windows too.
        var ws = Path.GetTempPath().Replace('\\', '/').TrimEnd('/');
        var exit = await fx.HandleAsync(
            $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","workspace_roots":["{{ws}}"]}""",
            budgetTotal: TimeSpan.FromSeconds(5));
        await Assert.That(exit).IsEqualTo(0);

        var stdout = capture.GetCapturedOutput();
        await Assert.That(stdout).StartsWith("""{"additional_context":""");
        await Assert.That(stdout).EndsWith("\"}\n");
        var node = JsonNode.Parse(stdout)!;
        var fragment = node["additional_context"]!.GetValue<string>();
        await Assert.That(fragment).Contains("Team memory");
    }

    // Qodo #1: mutates both process-global Console.Out AND AppConfig's resolved state
    // (ResolvedServerUrl/ResolvedProfile) — [NotInParallel] here matches this file's own
    // precedent for every other Console.Out-capturing test above (a bare, suite-wide
    // exclusion, not merely a named group, since other files elsewhere in the process also
    // mutate the same statics). The resolved state is captured up front and restored in the
    // finally below rather than being unconditionally reset to a fresh `new Profile()` —
    // AppConfig.SetResolvedState has no "clear"/"unset" primitive (see
    // AppConfigResolvedStateTests), so restoring means re-invoking it with the captured
    // original values, putting back exactly what was there rather than clobbering it.
    [Test, NotInParallel]
    public async Task DisableMemoryIndex_emits_empty_and_skips_provider() {
        using var fx = new Fixture();
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var originalServerUrl = AppConfig.ResolvedServerUrl;
        var originalResolved  = AppConfig.ResolvedProfile;
        AppConfig.SetResolvedState("http://localhost", "default", new Profile { DisableMemoryIndex = true });

        using var capture = ConsoleOutput.StartCapture();
        try {
            var sid = Guid.NewGuid().ToString("N");
            var exit = await fx.HandleAsync($$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""");

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
            await Assert.That(fx.MemoryIndexRequested).IsFalse();
        } finally {
            // Restore exactly what was resolved before this test ran. A null original
            // (AppConfig never touched yet in this process) has no public "unset" to restore
            // to, so it falls back to the same fresh default this test always used pre-fix.
            AppConfig.SetResolvedState(
                originalServerUrl ?? "http://localhost",
                originalResolved?.ProfileName ?? "default",
                originalResolved?.Profile ?? new Profile());
        }
    }

    [Test, NotInParallel]
    public async Task NoBudget_skips_provider() {
        using var fx = new Fixture();
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";
        var sid = Guid.NewGuid().ToString("N");

        using var capture = ConsoleOutput.StartCapture();
        // 500ms total is comfortably below HookBudget.Safety (1.5s), so
        // memBudget = budgetTotal - elapsed - Safety is guaranteed negative regardless
        // of how fast recording actually completes — no artificial per-POST delay needed.
        var exit = await fx.HandleAsync(
            $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""",
            budgetTotal: TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
    }

    // ---------------------------------------------------------------------------------
    // Measured payload contract. A Cursor sessionStart payload carries a NULL
    // transcript_path (verified against cursor-agent 2026.07.23-e383d2b; probe logs archived
    // under docs/probes/2026-07-30-cursor-subagent-hooks/), which is why the transcript-derived
    // subagent-classification arm has no producer. These pin that contract so the arm cannot be
    // quietly assumed live again. See
    // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
    // ---------------------------------------------------------------------------------

    [Test, NotInParallel]
    public async Task SessionStart_with_null_transcript_path_stays_top_level_and_writes_no_link_marker() {
        using var fx = new Fixture();
        var sid = Guid.NewGuid().ToString("N");
        var ws  = Path.GetTempPath().Replace('\\', '/').TrimEnd('/');
        // The REAL shape: transcript_path is JSON null at sessionStart.
        var payload = $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","transcript_path":null,"workspace_roots":["{{ws}}"]}""";

        using (ConsoleOutput.StartCapture()) {
            var exit = await fx.HandleAsync(payload, budgetTotal: TimeSpan.FromSeconds(5));
            await Assert.That(exit).IsEqualTo(0);
        }

        // No link was persisted, so nothing classified this session as a subagent child. Note
        // what is NOT claimed: this does not prove ResolveParent/SaveLink went unexecuted —
        // ResolveParent can run and return null, and SaveLink can run and swallow a write
        // failure. The assertion is about the persisted outcome only.
        await Assert.That(CursorLiveSubagentLinker.TryLoadLink(sid)).IsNull();
        // ...and it took the ordinary top-level route rather than the subagent divert.
        await Assert.That(fx.RouteOrder).Contains("session-start/cursor");
        await Assert.That(fx.RouteOrder).DoesNotContain("subagent-start");

        // HONEST SCOPE — this is an outcome pin, NOT a mutation-sensitive guard test for the
        // `!string.IsNullOrEmpty(transcriptPath)` conjunct. Verified by mutation: deleting that
        // conjunct leaves this test PASSING, because a null path also fails downstream
        // (DiscoverSiblingTranscripts finds no directory and Correlate cannot read a null path),
        // so the conjunct is not independently observable under the real payload.
        // What it does protect: any future change that classifies a sessionStart from some OTHER
        // source — deriving the transcripts dir from workspace_roots is the obvious candidate —
        // has to keep this session top-level, or this test fails.
    }

    [Test, NotInParallel]
    public async Task MemoryIndex_is_fetched_only_for_sessionStart() {
        var sid = Guid.NewGuid().ToString("N");
        var ws  = Path.GetTempPath().Replace('\\', '/').TrimEnd('/');
        const string body = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        using (ConsoleOutput.StartCapture()) {
            // Positive control FIRST. Without it this test could pass vacuously in an
            // environment where memory injection is disabled outright.
            using var starting = new Fixture();
            starting.MemoryIndexBody = body;
            await starting.HandleAsync(
                $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","workspace_roots":["{{ws}}"]}""",
                budgetTotal: TimeSpan.FromSeconds(5));
            await Assert.That(starting.MemoryIndexRequested).IsTrue();

            // A postToolUse carries workspace_roots too (measured), so the only thing keeping it
            // away from the orchestrator is the call-site guard.
            using var other = new Fixture();
            other.MemoryIndexBody = body;
            await other.HandleAsync(
                $$"""{"hook_event_name":"postToolUse","session_id":"{{Guid.NewGuid():N}}","workspace_roots":["{{ws}}"]}""",
                budgetTotal: TimeSpan.FromSeconds(5));
            await Assert.That(other.MemoryIndexRequested).IsFalse();
        }

        // NOTE what this does and does not establish. It pins the ORCHESTRATOR CALL-SITE GUARD,
        // an internal invariant. It does NOT prove ClassificationAuthoritative: true is
        // warranted — that additionally needs the external fact that a subagent child never
        // receives sessionStart, which is a vendor behaviour no unit test can establish.
    }

    [Test, NotInParallel]
    public async Task OncePerConversation() {
        using var fx = new Fixture();
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";
        var sid = Guid.NewGuid().ToString("N");
        // Real non-repo workspace root (system temp), forward-slashed for cross-platform JSON —
        // an absent root now skips injection.
        var ws = Path.GetTempPath().Replace('\\', '/').TrimEnd('/');
        var payload = $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","workspace_roots":["{{ws}}"]}""";

        using (var first = ConsoleOutput.StartCapture()) {
            // Generous budget — see Ready_fragment_emitted's comment on the tight ~0.5s margin
            // at the production 2s default under heavy CI/full-suite CPU contention.
            var exit1 = await fx.HandleAsync(payload, budgetTotal: TimeSpan.FromSeconds(5));
            await Assert.That(exit1).IsEqualTo(0);
            await Assert.That(first.GetCapturedOutput()).Contains("additional_context");
        }

        using (var second = ConsoleOutput.StartCapture()) {
            var exit2 = await fx.HandleAsync(payload, budgetTotal: TimeSpan.FromSeconds(5));
            await Assert.That(exit2).IsEqualTo(0);
            await Assert.That(second.GetCapturedOutput()).IsEqualTo("{}\n");
        }
    }

    [Test, NotInParallel]
    public async Task AbsentWorkspaceRoot_skips_provider_even_when_process_cwd_is_a_repo() {
        var originalCwd = Environment.CurrentDirectory;
        using var tmp = new TempDir();
        // A real git repo WITH a remote as the process cwd: were the guard missing, the shared
        // scope resolver's Directory.GetCurrentDirectory() fallback would derive THIS repo's scope
        // and fetch its (unrelated) memories into the Cursor session. The guard must prevent any fetch.
        var repoDir = MakeTempRepoWithRemote(tmp, "https://github.com/example/leak-check.git");
        using var capture = ConsoleOutput.StartCapture();
        try {
            Environment.CurrentDirectory = repoDir;
            using var fx = new Fixture();
            fx.MemoryIndexBody = "[]"; // decoy — never fetched because the guard short-circuits first
            var sid = Guid.NewGuid().ToString("N");

            // No workspace_roots field at all. Generous budget (see Ready_fragment_emitted's note
            // on the tight ~0.5s margin at the 2s default under full-suite CPU contention).
            var exit = await fx.HandleAsync(
                $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""",
                budgetTotal: TimeSpan.FromSeconds(5));

            await Assert.That(exit).IsEqualTo(0);
            // The guard means the provider is NEVER called when no authoritative workspace root is
            // supplied — so the process cwd's repo memories can never leak — and the response is {}.
            await Assert.That(fx.MemoryIndexRequested).IsFalse();
            await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        } finally {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    // Creates a throwaway git repo with a controlled origin remote so a test can put the process
    // cwd inside a repository the scope resolver would otherwise detect.
    static string MakeTempRepoWithRemote(TempDir tmp, string originUrl) {
        var root = tmp.CreateDir("repo");
        RunGit(root, "init", "-q");
        RunGit(root, "remote", "add", "origin", originUrl);
        return root;
    }

    static void RunGit(string cwd, params string[] args) {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.GetAwaiter().GetResult()}");
        _ = stdout.GetAwaiter().GetResult();
    }

    [Test, NotInParallel]
    public async Task NonGuidSessionId_emits_empty() {
        using var fx = new Fixture();
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        using var capture = ConsoleOutput.StartCapture();
        var exit = await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"not-a-guid"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
    }

    [Test, NotInParallel]
    public async Task CancelledFetch_leaves_lease_uncommitted() {
        using var fx = new Fixture();
        var sid = Guid.NewGuid().ToString("N");
        // Real non-repo workspace root (system temp), forward-slashed for cross-platform JSON —
        // an absent root now skips injection before the provider is ever reached.
        var ws = Path.GetTempPath().Replace('\\', '/').TrimEnd('/');
        var payload = $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","workspace_roots":["{{ws}}"]}""";
        var clock = new ManualTimeProvider();
        Func<SessionStartMemoryLeaseStore> storeFactory = () => new SessionStartMemoryLeaseStore(fx.MemoryStoreRoot, clock);

        // A memory-index client whose GET never completes on its own — it only ever resolves via
        // the budget-bound linked token handed to the request itself.
        var handler = new CancelAwareHandler();
        using var neverRespondingClient = new HttpClient(handler);

        // Cancellation is deterministic, not wall-clock: the generous budgets never fire; the handler
        // signals entry and the test cancels via Release() strictly after, so entry can't lose the
        // race the old 250ms budget used to. The stub resolver keeps a git spawn out of the path.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var call = CursorHookCommand.HandleCore(
            fx.Client, "http://localhost", new StringReader(payload), fx.Spool, TimeSpan.FromSeconds(60),
            memoryClientFactory: (_, _) => Task.FromResult(neverRespondingClient),
            memoryStoreFactory: storeFactory,
            memoryBudgetOverride: TimeSpan.FromSeconds(30),
            memoryScopeResolver: new StubScopeResolver());

        // Bounded so a future change that skips the fetch fails fast here instead of hanging.
        await handler.EnteredSignal.Task.WaitAsync(TimeSpan.FromSeconds(30));
        handler.Release();

        var exit1 = await call;
        elapsed.Stop();
        await Assert.That(exit1).IsEqualTo(0);
        // Entered rules out a skipped attempt; TokenWasCancelable proves the budget/deadline token is
        // actually plumbed into the request (not CancellationToken.None); Cancelled confirms the fetch
        // observed cancellation; and returning far inside the budgets leaves Release() as the cause.
        await Assert.That(handler.Entered).IsTrue();
        await Assert.That(handler.TokenWasCancelable).IsTrue();
        await Assert.That(handler.Cancelled).IsTrue();
        await Assert.That(elapsed.Elapsed.TotalSeconds).IsLessThan(10);

        // Advance well past the 30s lease duration so the still-"leased" (never committed —
        // the cancellation raced RetryAsync's own fencing too) record from the first attempt
        // is superseded rather than fencing a second attempt for 30 real seconds.
        clock.Advance(TimeSpan.FromSeconds(31));
        fx.MemoryIndexBody = "[]";

        var exit2 = await CursorHookCommand.HandleCore(
            fx.Client, "http://localhost", new StringReader(payload), fx.Spool, TimeSpan.FromSeconds(15),
            memoryStoreFactory: storeFactory,
            memoryBudgetOverride: TimeSpan.FromSeconds(10),
            memoryScopeResolver: new StubScopeResolver());
        await Assert.That(exit2).IsEqualTo(0);
        // The index GET fires again on fx.Client — proving the first, cancelled attempt's
        // lease was never spent as "completed".
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
    }

    // The other side of that guard, asserted rather than left to a runner: no budget left means no
    // fetch, and the sessionStart still emits its single {}.
    [Test, NotInParallel]
    public async Task ExhaustedMemoryBudget_skips_the_fetch_and_still_emits() {
        using var fx = new Fixture();
        var sid = Guid.NewGuid().ToString("N");
        var ws = Path.GetTempPath().Replace('\\', '/').TrimEnd('/');
        var payload = $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","workspace_roots":["{{ws}}"]}""";
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var storeBuilt = false;

        using var capture = ConsoleOutput.StartCapture();
        var exit = await CursorHookCommand.HandleCore(
            fx.Client, "http://localhost", new StringReader(payload), fx.Spool, TimeSpan.FromSeconds(15),
            memoryStoreFactory: () => {
                storeBuilt = true;
                return new SessionStartMemoryLeaseStore(fx.MemoryStoreRoot, new ManualTimeProvider());
            },
            memoryBudgetOverride: TimeSpan.Zero);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        // Pins THIS guard specifically: the provider would also decline a zero budget, but only
        // the orchestration guard returns before the store is built.
        await Assert.That(storeBuilt).IsFalse();
    }

    // Task 1: the fixture must be able to serve GET /api/memories/index
    // distinctly from the generic transcript-watermark GET (which stays 404) — the
    // seam later tasks rely on to fake the memory-index endpoint. No production
    // wiring exists yet (HandleCore doesn't call this route on its own); this only
    // proves the test double is capable of it.
    [Test]
    public async Task memory_index_endpoint_is_routed_distinctly_from_the_watermark_GET() {
        using var fx = new Fixture();
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        // Drive a sessionStart through the normal fixture path — no behavior change yet.
        var exit = await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc"}""");
        await Assert.That(exit).IsEqualTo(0);

        using var resp = await fx.Client.GetAsync("http://localhost/api/memories/index");
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await resp.Content.ReadAsStringAsync()).IsEqualTo(fx.MemoryIndexBody);

        // The generic watermark GET path is untouched — still 404.
        using var watermarkResp = await fx.Client.GetAsync("http://localhost/api/sessions/abc/transcript-watermark");
        await Assert.That(watermarkResp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task legacy_cursor_spool_is_transformed_and_merged() {
        using var tmp = new TempDir();
        var legacyDir = tmp.CreateDir("legacy");
        var spoolDir  = tmp.CreateDir("spool");
        // Old format: {hook_event_name, body}
        legacyDir.CreateFile($"{Sid}.jsonl",
            $"{{\"hook_event_name\":\"sessionEnd\",\"body\":\"{{\\\"session_id\\\":\\\"{Sid}\\\"}}\"}}\n");

        var spool = new HookSpool(spoolDir);
        CursorHookCommand.MigrateLegacyCursorSpool(spool, legacyDir);

        var migrated = await File.ReadAllTextAsync(spoolDir.PathTo($"{Sid}.jsonl"));
        await Assert.That(migrated).Contains("\"route\":\"session-end/cursor\"");
        await Assert.That(File.Exists(legacyDir.PathTo($"{Sid}.jsonl"))).IsFalse();
    }

    sealed class Fixture : IDisposable {
        readonly string _tmpHome = Path.Combine(
            Path.GetTempPath(),
            $"kcap-cursor-hook-test-{Guid.NewGuid().ToString("N")[..8]}"
        );

        readonly string _spoolPath;
        readonly string _transcriptPath;

        public List<string> Sent       { get; } = [];
        public List<string> RouteOrder { get; } = [];
        public HookSpool    Spool      { get; }
        public TimeSpan     HoldOnPost { get; set; } = TimeSpan.Zero;

        // Lets a test fake the shared SessionStart memory-index endpoint
        // distinctly from the generic transcript-watermark GET (which stays 404).
        public string         MemoryIndexBody      { get; set; } = "[]";
        public HttpStatusCode MemoryIndexStatus    { get; set; } = HttpStatusCode.OK;
        public bool           MemoryIndexRequested { get; private set; }
        public Uri?           MemoryIndexRequestUri { get; private set; }

        public HttpClient Client                { get; }
        public string     TranscriptPathEscaped => _transcriptPath.Replace(@"\", @"\\");

        // Task 10: the backfill now holds a non-newline-terminated final line on every
        // mid-session (Hold-policy) call — a real Cursor transcript line is newline-terminated
        // once flushed, so tests write content the same way rather than exercising the
        // holdback edge case incidentally.
        public Task WriteTranscript(string content) =>
            File.WriteAllTextAsync(_transcriptPath, content.EndsWith('\n') ? content : content + "\n");

        public IEnumerable<string> SpoolFiles =>
            Directory.Exists(_spoolPath) ? Directory.EnumerateFiles(_spoolPath, "*.jsonl") : [];

        public Fixture(HttpStatusCode postStatus = HttpStatusCode.OK) {
            Directory.CreateDirectory(_tmpHome);
            _spoolPath      = Path.Combine(_tmpHome, "spool");
            _transcriptPath = Path.Combine(_tmpHome, "transcript.jsonl");
            Spool           = new HookSpool(_spoolPath);

            var handler = new StubHandler(async req => {
                    var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                    var path = req.RequestUri!.AbsolutePath;
                    Sent.Add($"{path}|{body}");

                    if (path.StartsWith("/hooks/", StringComparison.Ordinal)) {
                        RouteOrder.Add(path.Replace("/hooks/", ""));
                    }

                    // The shared SessionStart memory-index GET is routed distinctly
                    // from the generic transcript-watermark GET below (which stays 404).
                    if (path == "/api/memories/index") {
                        MemoryIndexRequested  = true;
                        MemoryIndexRequestUri = req.RequestUri;
                        return new HttpResponseMessage(MemoryIndexStatus) {
                            Content = new StringContent(MemoryIndexBody, Encoding.UTF8, "application/json")
                        };
                    }

                    // GET watermark — return 404 so transcript backfill is a no-op without
                    // tripping the fail-open path.
                    if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);

                    if (HoldOnPost > TimeSpan.Zero) {
                        await Task.Delay(HoldOnPost);
                    }

                    return new HttpResponseMessage(postStatus);
                }
            );
            Client = new HttpClient(handler);
        }

        // Isolate every fixture-routed test's SessionStart memory lease store to its own
        // temp dir (mirrors ClaudeHookCommandTests.Fixture) — otherwise a successful
        // sessionStart with a GUID session_id would touch the real per-machine default
        // store root. Exposed so a test needing a controllable clock (e.g. a lease that
        // must be treated as expired without a real 30s wait) can build its own store
        // against the SAME root with a custom TimeProvider.
        public string MemoryStoreRoot => Path.Combine(_tmpHome, "memory");

        public Task<int> HandleAsync(string stdin, TimeSpan? budgetTotal = null,
                Func<SessionStartMemoryLeaseStore>? memoryStoreFactory = null) =>
            CursorHookCommand.HandleCore(
                Client,
                baseUrl: "http://localhost",
                stdin: new StringReader(stdin),
                spool: Spool,
                budgetTotal: budgetTotal ?? TimeSpan.FromSeconds(2),
                memoryStoreFactory: memoryStoreFactory ?? (() => new SessionStartMemoryLeaseStore(MemoryStoreRoot))
            );

        public string SentToHook(string segment) =>
            Sent.First(s => s.StartsWith($"/hooks/{segment}", StringComparison.Ordinal)).Split('|', 2)[1];

        public IEnumerable<string> AllSentTo(string segment) =>
            Sent.Where(s => s.StartsWith($"/hooks/{segment}", StringComparison.Ordinal)).Select(s => s.Split('|', 2)[1]);

        public void Dispose() {
            Client.Dispose();
            try { Directory.Delete(_tmpHome, true); } catch { }
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            impl(request);
    }

    // Ignores the passed CancellationToken entirely — simulates a stdin read that would
    // hang regardless of the dispatcher deadline, so only the OUTER Task.WhenAny race
    // (not the inner read noticing cancellation) can possibly account for a prompt return.
    sealed class NeverCompletingReader : TextReader {
        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) =>
            Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None).ContinueWith(_ => "", TaskScheduler.Default);
    }

    // Honours the passed CancellationToken properly (unlike NeverCompletingReader above) —
    // it only ever completes by observing the token fire, then signals `Cancelled` before
    // throwing. Proves HandleCore's deadline branch deterministically cancels the abandoned
    // inner rather than merely disposing its CTS.
    sealed class CancelObservingReader : TextReader {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<string> ReadToEndAsync(CancellationToken cancellationToken) {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (cancellationToken.Register(() => tcs.TrySetResult())) {
                await tcs.Task;
            }
            Cancelled.TrySetResult();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    // Settable clock for SessionStartMemoryLeaseStore tests — lets a test fast-forward past
    // a 30s lease-expiry fence without a real wait. A local, test-file-scoped equivalent of
    // the foundation suite's own private ManualTimeProvider (that one isn't shared/exported).
    sealed class ManualTimeProvider : TimeProvider {
        DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // Resolves instantly to a fixed scope. The real resolver spawns git, whose wall-clock cost
    // would otherwise come out of the memory budget a test is trying to spend on the fetch.
    sealed class StubScopeResolver : ISessionStartMemoryScopeResolver {
        public Task<SessionStartMemoryScope> ResolveAsync(string? cwd, TimeSpan budget, CancellationToken ct) =>
            Task.FromResult(new SessionStartMemoryScope(RepoHash: null, MachineTag: "test-machine"));
    }

    // A GET that never resolves on its own; cancellation is test-driven (EnteredSignal → Release())
    // rather than racing a real-time budget. It records whether the request's own token is cancelable
    // so the test can prove the budget/deadline token is actually plumbed into the request.
    sealed class CancelAwareHandler : HttpMessageHandler {
        readonly CancellationTokenSource _release = new();
        volatile bool _entered;
        volatile bool _cancelled;
        volatile bool _tokenWasCancelable;
        // Separate "never fetched at all", "fetched and abandoned", and "fetched and cancelled".
        public bool Entered => _entered;
        public bool Cancelled => _cancelled;
        // False would mean production passed CancellationToken.None — the budget/deadline token was
        // not plumbed into the request.
        public bool TokenWasCancelable => _tokenWasCancelable;
        // Fires the moment SendAsync is entered, so the test can cancel strictly after entry.
        public TaskCompletionSource EnteredSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Deterministically cancel the in-flight request. Call only once EnteredSignal has fired.
        public void Release() => _release.Cancel();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            _entered = true;
            _tokenWasCancelable = ct.CanBeCanceled;
            EnteredSignal.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _release.Token);
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            } catch (OperationCanceledException) {
                _cancelled = true;
                throw;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) _release.Dispose();
            base.Dispose(disposing);
        }
    }
}
