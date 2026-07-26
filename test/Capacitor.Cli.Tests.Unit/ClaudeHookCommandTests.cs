using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class ClaudeHookCommandTests {
    const string Sid = "9dc2775376454e4691ecc2d69973c152";

    [Test]
    public async Task session_start_posts_to_session_start_route() {
        using var fx = new Fixture();
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("session-start");
    }

    [Test]
    public async Task memory_store_initialization_failure_does_not_suppress_session_start_capture() {
        using var fx = new Fixture();

        var exit = await ClaudeHookCommand.HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, System.Diagnostics.Stopwatch.GetTimestamp(),
            "http://localhost", new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            memoryStoreFactory: () => throw new UnauthorizedAccessException("read-only store"));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.RouteOrder).Contains("session-start");
    }

    [Test, NotInParallel]
    public async Task disabled_memory_index_does_not_construct_the_lease_store() {
        using var fx = new Fixture();
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]"""; // decoy — must never be fetched
        var storeConstructed = false;

        var exit = await WithProfileAsync(new Profile { DisableMemoryIndex = true }, () =>
            ClaudeHookCommand.HandleCore(
                fx.Client, AuthStatus.Ok, fx.Spool, System.Diagnostics.Stopwatch.GetTimestamp(),
                "http://localhost", new StringReader(
                    $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),
                memoryStoreFactory: () => {
                    storeConstructed = true;
                    return new SessionStartMemoryLeaseStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
                }));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(storeConstructed).IsFalse();
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
        await Assert.That(fx.RouteOrder).Contains("session-start");
    }

    // ── SessionStart team-memory index: behavioral baseline ─────────────────────────────────
    // Characterizes today's byte-level SessionStart output on the shared
    // SessionStartMemoryOrchestrator/ContextProvider/LeaseStore foundation (StartMemoryIndexTask
    // below) — memory-index GET runs parallel with the session-start POST, joined within the hook
    // budget, composed with lessons/version-nudge into one hookSpecificOutput.additionalContext
    // envelope — so a future change to that wiring can't silently regress it.

    [Test, NotInParallel]
    public async Task session_start_joins_lessons_nudge_and_memory_fragments_in_order() {
        using var fx = new Fixture();
        const string responseJson =
            """{"top_clusters":[{"text":"seal secrets","category":"safety"},{"text":"run tests first","category":"agent_guidance"}],"version":"999.999.999"}""";
        fx.RespondJson = responseJson;
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await WithProfileAsync(new Profile(), () => RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""")));
        await Assert.That(exit).IsEqualTo(0);

        var responseNode     = JsonNode.Parse(responseJson);
        var expectedLessons  = SessionGuidelinesEmitter.BuildFragment(responseNode, disabled: false);
        var expectedNudge    = VersionNudgeEmitter.BuildFragment(responseNode, CapacitorVersion.CurrentDisplay());
        var expectedMemory   = MemoryIndexEmitter.BuildFragment(JsonNode.Parse(fx.MemoryIndexBody), disabled: false);
        var expectedEnvelope = SessionStartAdditionalContext.BuildEnvelope(expectedLessons, expectedNudge, expectedMemory);

        // Byte-exact: today's wiring order is lessons, then nudge, then memory — joined by
        // BuildEnvelope and written via a single Console.WriteLine (hence the trailing "\n").
        await Assert.That(stdout).IsEqualTo(expectedEnvelope + "\n");

        var ctx        = JsonNode.Parse(stdout)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        var lessonsIdx = ctx.IndexOf("## Known patterns", StringComparison.Ordinal);
        var nudgeIdx   = ctx.IndexOf("newer kcap version", StringComparison.Ordinal);
        var memoryIdx  = ctx.IndexOf("Team memory", StringComparison.Ordinal);
        await Assert.That(lessonsIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(nudgeIdx).IsGreaterThan(lessonsIdx);
        await Assert.That(memoryIdx).IsGreaterThan(nudgeIdx);
    }

    [Test, NotInParallel]
    public async Task session_start_with_only_a_ready_memory_index_emits_just_the_memory_fragment() {
        using var fx = new Fixture();
        fx.RespondJson = "{}"; // no top_clusters/version — lessons and nudge fragments are both null
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await WithProfileAsync(new Profile(), () => RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""")));
        await Assert.That(exit).IsEqualTo(0);

        var expectedMemory   = MemoryIndexEmitter.BuildFragment(JsonNode.Parse(fx.MemoryIndexBody), disabled: false);
        var expectedEnvelope = SessionStartAdditionalContext.BuildEnvelope(null, null, expectedMemory);
        await Assert.That(stdout).IsEqualTo(expectedEnvelope + "\n");
        await Assert.That(stdout).Contains("Team memory");
    }

    [Test, NotInParallel]
    public async Task session_start_with_an_empty_memory_index_array_emits_nothing() {
        // CompleteWithoutContext disposition (a successful, empty fetch) — with no lessons/nudge
        // either, BuildEnvelope collapses to null and NOTHING is written to stdout at all.
        using var fx = new Fixture();
        fx.RespondJson = "{}";
        fx.MemoryIndexBody = "[]";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await WithProfileAsync(new Profile(), () => RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""")));
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
        await Assert.That(stdout).IsEqualTo("");
    }

    [Test, NotInParallel]
    public async Task session_start_with_a_204_memory_index_response_emits_nothing() {
        // The provider special-cases 204 NoContent as CompleteWithoutContext without even
        // reading a body.
        using var fx = new Fixture();
        fx.RespondJson = "{}";
        fx.MemoryIndexStatus = HttpStatusCode.NoContent;
        fx.MemoryIndexBody = "";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await WithProfileAsync(new Profile(), () => RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""")));
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
        await Assert.That(stdout).IsEqualTo("");
    }

    [Test, NotInParallel]
    public async Task session_start_with_a_5xx_memory_index_response_emits_nothing_and_does_not_fail_the_hook() {
        // RetryableFailure disposition — fail-open: the hook still succeeds and nothing about
        // the memory fetch surfaces in the envelope (there is none, since lessons/nudge are
        // absent here too).
        using var fx = new Fixture();
        fx.RespondJson = "{}";
        fx.MemoryIndexStatus = HttpStatusCode.InternalServerError;
        fx.MemoryIndexBody = "";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await WithProfileAsync(new Profile(), () => RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""")));
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
        await Assert.That(stdout).IsEqualTo("");
    }

    [Test, NotInParallel]
    public async Task memory_fetch_is_not_repeated_on_a_second_session_start_for_the_same_session() {
        // Once the shared lease store commits a disposition — Ready OR CompleteWithoutContext —
        // for a session_id, a later SessionStart for that SAME session never re-fetches: a
        // resolved, non-repeating lifecycle is exactly-once, not "repeat until non-empty".
        using var fx = new Fixture();
        fx.RespondJson = "{}";
        fx.MemoryIndexBody = "[]"; // CompleteWithoutContext on the first call
        var sid = Guid.NewGuid().ToString("N");
        var payload = $$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""";

        var (exit1, stdout1) = await WithProfileAsync(new Profile(), () => RunCapturingStdoutAsync(() => fx.HandleAsync(payload)));
        await Assert.That(exit1).IsEqualTo(0);
        await Assert.That(stdout1).IsEqualTo("");
        await Assert.That(fx.MemoryIndexRequestCount).IsEqualTo(1);

        // A decoy non-empty index — if the second call re-fetched, this WOULD surface.
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var (exit2, stdout2) = await WithProfileAsync(new Profile(), () => RunCapturingStdoutAsync(() => fx.HandleAsync(payload)));
        await Assert.That(exit2).IsEqualTo(0);
        await Assert.That(stdout2).IsEqualTo("");
        await Assert.That(fx.MemoryIndexRequestCount).IsEqualTo(1); // NOT re-fetched
    }

    [Test, NotInParallel]
    public async Task memory_index_get_timing_out_does_not_suppress_lessons_or_nudge() {
        // The memory-index GET runs in parallel with the POST and is joined ONLY within the
        // remaining hook budget: a GET that outlives that budget yields a null fragment
        // (fail-open) without delaying — or breaking — the lessons fragment the same response
        // already carries.
        using var fx = new Fixture();
        fx.MemoryIndexDelay = TimeSpan.FromSeconds(30); // never resolves inside the session-start budget
        fx.RespondJson = """{"top_clusters":[{"text":"seal secrets","category":"safety"}]}""";

        // Default (near-"now") processStart, exactly like this file's other hung-server tests
        // (e.g. subagent_stop_against_hung_server_is_spooled_within_budget) — the full ~3.5s of
        // session-start's usable budget (5s ceiling minus the 1.5s safety margin) is comfortably
        // enough for watcher-start + repo enrichment + the fast, undelayed POST, but far short of
        // the 30s memory-index delay above.
        var sid = Guid.NewGuid().ToString("N");
        var sw  = System.Diagnostics.Stopwatch.StartNew();
        var (exit, stdout) = await WithProfileAsync(new Profile(), () => RunCapturingStdoutAsync(() =>
            fx.HandleAsync(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""")));
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5)); // did not wait out the 30s delay
        await Assert.That(stdout).Contains("## Known patterns"); // lessons fragment still injected
        await Assert.That(stdout).DoesNotContain("Team memory"); // memory fragment never joined in time
    }

    [Test, NotInParallel]
    public async Task exhausted_budget_before_the_memory_task_starts_never_touches_the_provider() {
        // When HookBudget.Remaining("session-start") is already <= 0 by the time
        // StartMemoryIndexTask is reached, the memory subsystem must never touch the network at
        // all (same short-circuit as the `disabled` guard) — and, at that point, neither can the
        // session-start POST itself, which the ordering/spool path below already covers.
        using var fx = new Fixture();
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]"""; // decoy
        fx.RespondJson = """{"top_clusters":[{"text":"seal secrets","category":"safety"}]}""";

        var processStart = System.Diagnostics.Stopwatch.GetTimestamp()
                         - (long)(4 * System.Diagnostics.Stopwatch.Frequency);

        var sid = Guid.NewGuid().ToString("N");
        var exit = await fx.HandleAsync(
            $$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""",
            processStart);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
    }

    [Test, NotInParallel]
    public async Task memory_index_ready_is_discarded_when_the_session_start_post_fails() {
        // GET-succeeds-but-POST-fails: the POST failure short-circuits BEFORE the response is
        // ever read, so no envelope is built at all — even a Ready memory fragment never
        // surfaces. The memory task may be left running in the background (abandoned).
        using var fx = new Fixture(HttpStatusCode.InternalServerError);
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await WithProfileAsync(new Profile(), () => RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""")));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout).IsEqualTo("");
        await Assert.That(fx.SpoolFiles.Any()).IsTrue(); // still durably spooled for retry
    }

    /// <summary>Runs <paramref name="action"/> with <see cref="AppConfig.ResolvedProfile"/> set to
    /// <paramref name="profile"/>, restoring whatever was resolved before (or the closest
    /// equivalent to "untouched" — see <c>AppConfig.SetResolvedState</c>'s lack of an "unset"
    /// primitive) regardless of run order or a mid-test exception.</summary>
    static async Task<T> WithProfileAsync<T>(Profile profile, Func<Task<T>> action) {
        var originalServerUrl = AppConfig.ResolvedServerUrl;
        var originalResolved  = AppConfig.ResolvedProfile;
        AppConfig.SetResolvedState("http://localhost", "default", profile);
        try {
            return await action();
        } finally {
            AppConfig.SetResolvedState(
                originalServerUrl ?? "http://localhost",
                originalResolved?.ProfileName ?? "default",
                originalResolved?.Profile ?? new Profile());
        }
    }

    /// <summary>Redirects <see cref="Console.Out"/> to a buffer for the duration of
    /// <paramref name="action"/> (a fresh <see cref="StringWriter"/> with <c>NewLine = "\n"</c> so
    /// the captured bytes are platform-independent), restoring the original writer even if
    /// <paramref name="action"/> throws.</summary>
    static async Task<(int Exit, string Stdout)> RunCapturingStdoutAsync(Func<Task<int>> action) {
        var originalOut = Console.Out;
        var writer = new StringWriter { NewLine = "\n" };
        try {
            Console.SetOut(writer);
            var exit = await action();
            return (exit, writer.ToString());
        } finally {
            Console.SetOut(originalOut);
        }
    }

    // the session-start payload gains a best-effort workspace_root (the git repo root
    // for cwd), used server-side by plan-artifact discovery. Fail-open: a cwd with no
    // discoverable .git entry (e.g. "/tmp") must omit the field entirely rather than send null.
    [Test]
    public async Task session_start_includes_workspace_root_when_cwd_is_inside_a_git_repo() {
        var tmp = Directory.CreateTempSubdirectory("kcap-claude-hook-git-");
        try {
            Directory.CreateDirectory(Path.Combine(tmp.FullName, ".git"));
            var nested = Path.Combine(tmp.FullName, "nested", "dir");
            Directory.CreateDirectory(nested);

            using var fx = new Fixture();
            await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"{{nested.Replace("\\", "\\\\")}}"}""");

            var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-start|"));
            var body   = JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..]);
            await Assert.That(body!["workspace_root"]?.GetValue<string>()).IsEqualTo(tmp.FullName);
        } finally {
            // Best-effort: on windows-latest runners the AV/indexer can transiently hold a
            // handle on a just-created directory and fail the recursive delete with
            // IOException ("being used by another process"). The temp dir is on an
            // ephemeral runner — retry briefly, then let it go rather than fail the test.
            for (var attempt = 1; ; attempt++) {
                try {
                    tmp.Delete(recursive: true);
                    break;
                } catch (IOException) when (attempt < 4) {
                    await Task.Delay(100 * attempt);
                } catch (IOException) {
                    break;
                } catch (UnauthorizedAccessException) {
                    break;
                }
            }
        }
    }

    [Test]
    public async Task session_start_omits_workspace_root_when_cwd_has_no_git_repo() {
        using var fx = new Fixture();
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}""");

        var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-start|"));
        var body   = JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..]);
        await Assert.That(body!["workspace_root"]).IsNull();
    }

    // Covers the auth-hang case from the spec: the hard cap must beat an
    // uncancellable hang (e.g. TokenStore.RefreshAsync's untimed HttpClient.PostAsync).
    [Test]
    public async Task hard_cap_returns_zero_when_inner_ignores_cancellation() {
        var inner = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(10)); return 42; });
        var sw    = System.Diagnostics.Stopwatch.StartNew();
        var exit  = await ClaudeHookCommand.WithHardCap(inner, TimeSpan.FromMilliseconds(50));
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task hard_cap_returns_inner_result_when_inner_finishes_first() {
        var exit = await ClaudeHookCommand.WithHardCap(Task.FromResult(7), TimeSpan.FromSeconds(2));
        await Assert.That(exit).IsEqualTo(7);
    }

    [Test]
    public async Task session_end_on_5xx_is_spooled_and_returns_zero() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError);
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp","reason":"other"}""");
        await Assert.That(exit).IsEqualTo(0);
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-end\"");
        await Assert.That(content).Contains("ended_at");
    }

    [Test]
    public async Task session_end_against_hung_server_is_spooled_within_budget() {
        using var fx = new Fixture();
        fx.HoldOnPost = TimeSpan.FromSeconds(30); // server hangs past the bounded attempt
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // processStart in the recent past leaves a small remaining budget.
        var exit = await fx.HandleAsync(
            $$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(15)); // did not wait the full 30s
        await Assert.That(fx.SpoolFiles.Any()).IsTrue();
    }

    [Test]
    public async Task session_end_on_4xx_is_not_spooled() {
        using var fx = new Fixture(HttpStatusCode.BadRequest);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task session_start_on_failure_is_spooled_with_minimal_body() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp","source":"startup"}""");
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-start\"");
        await Assert.That(JsonNode.Parse(JsonNode.Parse(content.Split('\n')[0])!["body"]!.GetValue<string>())!["session_id"]!.GetValue<string>())
            .IsEqualTo(Sid);
    }

    [Test]
    public async Task pending_backlog_is_drained_on_next_hook_when_server_up() {
        using var fx = new Fixture(); // 200 OK
        fx.Spool.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");
        // A fresh, unrelated stop hook with the server up flushes the backlog.
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("session-end"); // replayed
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();          // delivered + cleaned
    }

    [Test]
    public async Task current_session_start_replays_before_its_session_end() {
        using var fx = new Fixture();
        fx.Spool.Append(Sid, "session-start", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        var startIdx = fx.RouteOrder.IndexOf("session-start");
        var endIdx   = fx.RouteOrder.IndexOf("session-end");
        await Assert.That(startIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(endIdx).IsGreaterThan(startIdx);
    }

    // CRITICAL 1: bound client creation. If CreateAuthenticatedClientAsync hangs (untimed
    // /auth/config GET or token refresh during an outage) past the hook budget, the lifecycle
    // event must still be spooled — spooling is a local disk write that needs no client.
    [Test]
    public async Task session_end_spooled_when_client_creation_exceeds_budget() {
        using var fx = new Fixture();
        // Slow factory: never completes within the cap (30s) so the budget elapses first.
        Func<Task<(HttpClient, AuthStatus)>> slowFactory = () =>
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => (new HttpClient(), AuthStatus.Ok), TaskScheduler.Default);

        // processStart ~13.4s in the past → session-end remaining = 15 - 13.4 - 1.5 ≈ 0.1s cap.
        var processStart = System.Diagnostics.Stopwatch.GetTimestamp()
                         - (long)(13.4 * System.Diagnostics.Stopwatch.Frequency);

        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await ClaudeHookCommand.HandleWithDeps(
            fx.Spool, processStart, "http://localhost",
            new StringReader($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}"""),
            updateCheckTask: null, clientFactory: slowFactory);
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(10)); // well under the 15s ceiling, not the 30s factory
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-end\"");
    }

    [Test]
    public async Task create_client_within_budget_returns_null_when_factory_slower_than_cap() {
        Func<Task<(HttpClient, AuthStatus)>> slow = () =>
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => (new HttpClient(), AuthStatus.Ok), TaskScheduler.Default);
        var sw     = System.Diagnostics.Stopwatch.StartNew();
        var result = await ClaudeHookCommand.CreateClientWithinBudgetAsync(slow, TimeSpan.FromMilliseconds(50));
        sw.Stop();
        await Assert.That(result).IsNull();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task create_client_within_budget_returns_client_when_factory_fast() {
        var made   = new HttpClient();
        var result = await ClaudeHookCommand.CreateClientWithinBudgetAsync(() => Task.FromResult((made, AuthStatus.Ok)), TimeSpan.FromSeconds(2));
        await Assert.That(result).IsNotNull();
        await Assert.That(ReferenceEquals(result!.Value.Client, made)).IsTrue();
        result.Value.Client.Dispose();
    }

    const string AgentId = "a1b2c3d4";

    [Test]
    public async Task subagent_stop_on_5xx_is_spooled_and_returns_zero() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError);
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(exit).IsEqualTo(0);
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"subagent-stop\"");
    }

    [Test]
    public async Task subagent_stop_against_hung_server_is_spooled_within_budget() {
        using var fx = new Fixture();
        fx.HoldOnPost = TimeSpan.FromSeconds(30); // server hangs past the bounded attempt
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5)); // did not wait the full 30s
        await Assert.That(fx.SpoolFiles.Any()).IsTrue();
    }

    [Test]
    public async Task subagent_stop_on_4xx_is_not_spooled() {
        using var fx = new Fixture(HttpStatusCode.BadRequest);
        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task subagent_stop_without_agent_id_is_not_spooled() {
        // No agent_id → no SubagentCompleted to deliver → unchanged shared-path behavior (no spool).
        using var fx = new Fixture(); // OK
        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task spooled_subagent_stop_is_replayed_on_next_hook() {
        using var fx = new Fixture(); // server up
        fx.Spool.Append(Sid, "subagent-stop", $$"""{"session_id":"{{Sid}}","agent_id":"{{AgentId}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("subagent-stop"); // drained + replayed
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();           // delivered + cleaned
    }

    [Test]
    public async Task replayed_session_end_with_generate_whats_done_is_handled() {
        // Server returns generate_whats_done:false for the replayed session-end (set false to avoid process spawn).
        using var fx = new Fixture();
        fx.RespondJson = """{"generate_whats_done":false}""";
        fx.Spool.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task subagent_stop_spooled_when_client_creation_exceeds_budget() {
        using var fx = new Fixture();
        Func<Task<(HttpClient, AuthStatus)>> slowFactory = () =>
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => (new HttpClient(), AuthStatus.Ok), TaskScheduler.Default);
        // processStart ~3.4s in the past → subagent-stop remaining = 5 - 3.4 - 1.5 ≈ 0.1s cap.
        var processStart = System.Diagnostics.Stopwatch.GetTimestamp()
                         - (long)(3.4 * System.Diagnostics.Stopwatch.Frequency);
        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await ClaudeHookCommand.HandleWithDeps(
            fx.Spool, processStart, "http://localhost",
            new StringReader($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}"""),
            updateCheckTask: null, clientFactory: slowFactory);
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"subagent-stop\"");
    }

    [Test]
    public async Task current_session_start_replays_before_subagent_stop() {
        using var fx = new Fixture(); // server up
        fx.Spool.Append(Sid, "session-start", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        var startIdx = fx.RouteOrder.IndexOf("session-start");
        var stopIdx  = fx.RouteOrder.IndexOf("subagent-stop");
        await Assert.That(startIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(stopIdx).IsGreaterThan(startIdx);
    }

    [Test]
    public async Task subagent_stop_spooled_not_posted_when_current_session_backlog_remains() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError); // drain fails transiently → backlog remains
        fx.Spool.Append(Sid, "session-start", $$"""{"session_id":"{{Sid}}"}""");

        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");

        // The drain attempted the stranded session-start (and failed transiently, leaving backlog).
        await Assert.That(fx.RouteOrder).Contains("session-start");
        // Ordering guard fired: the fresh subagent-stop was spooled, NOT posted — so it never
        // appears in RouteOrder. (Without the guard it would be POSTed before this session's
        // stranded session-start is delivered.)
        await Assert.That(fx.RouteOrder).DoesNotContain("subagent-stop");
        // ...and it is durably spooled.
        var all = string.Concat(fx.SpoolFiles.Select(File.ReadAllText));
        await Assert.That(all).Contains("\"route\":\"subagent-stop\"");
    }

    // Task 12 / BLOCKER-1+3 regression: the centralized ordered drain (now running on every
    // non-Codex hook, incl. --claude) can WITHHOLD a spooled session-end in the ".ordered-*" temp
    // namespace pending the transcript tail. ClaudeHookCommand.CurrentSessionHasBacklog must see that
    // withheld terminal (it now delegates to HookSpool.HasBacklog, which covers ".ordered-*") so a
    // later Claude subagent-stop for the SAME session spools BEHIND it rather than POSTing ahead of
    // the still-withheld session-end — the exact cross-spool ordering violation the blockers prevent.
    [Test]
    public async Task subagent_stop_spools_behind_a_session_end_withheld_in_the_ordered_namespace() {
        using var fx = new Fixture(HttpStatusCode.OK); // server up — only the ordering guard can hold the post back
        // A withheld ordered-drain remainder, exactly as LifecycleSpoolDrain/DrainRoutesAsync leaves it.
        fx.WriteOrderedTemp(Sid, """{"route":"session-end","body":"{\"session_id\":\"withheld\"}"}""");

        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");

        // The guard saw the .ordered-* backlog: the fresh subagent-stop was spooled, NOT posted.
        await Assert.That(fx.RouteOrder).DoesNotContain("subagent-stop");
        var all = string.Concat(fx.SpoolFiles.Select(File.ReadAllText));
        await Assert.That(all).Contains("\"route\":\"subagent-stop\"");
    }

    sealed class Fixture : IDisposable {
        readonly string _tmpHome = Path.Combine(Path.GetTempPath(), $"kcap-claude-hook-{Guid.NewGuid():N}");
        readonly string _spoolPath;
        public List<string> Sent { get; } = [];
        public List<string> RouteOrder { get; } = [];
        public HookSpool Spool { get; }
        public HttpClient Client { get; }
        public TimeSpan HoldOnPost { get; set; } = TimeSpan.Zero;
        public string? RespondJson { get; set; }
        readonly HttpStatusCode _postStatus;

        // Lets a test fake the shared SessionStart memory-index endpoint distinctly from the
        // generic GET fallback below (which stays 404) — mirrors CursorHookCommandTests.Fixture.
        public string         MemoryIndexBody         { get; set; } = "[]";
        public HttpStatusCode MemoryIndexStatus        { get; set; } = HttpStatusCode.OK;
        public TimeSpan       MemoryIndexDelay         { get; set; } = TimeSpan.Zero;
        public bool           MemoryIndexRequested     => MemoryIndexRequestCount > 0;
        public int            MemoryIndexRequestCount  { get; private set; }

        public Fixture(HttpStatusCode postStatus = HttpStatusCode.OK) {
            Directory.CreateDirectory(_tmpHome);
            _spoolPath  = Path.Combine(_tmpHome, "spool");
            _postStatus = postStatus;
            Spool = new HookSpool(_spoolPath);
            Client = new HttpClient(new StubHandler(async (req, ct) => {
                var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                var path = req.RequestUri!.AbsolutePath;
                Sent.Add($"{path}|{body}");
                if (path.StartsWith("/hooks/")) RouteOrder.Add(path.Replace("/hooks/", ""));
                if (path == "/api/memories/index") {
                    MemoryIndexRequestCount++;
                    if (MemoryIndexDelay > TimeSpan.Zero) await Task.Delay(MemoryIndexDelay, ct);
                    return new HttpResponseMessage(MemoryIndexStatus) {
                        Content = new System.Net.Http.StringContent(MemoryIndexBody, System.Text.Encoding.UTF8, "application/json")
                    };
                }
                if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (HoldOnPost > TimeSpan.Zero) await Task.Delay(HoldOnPost, ct);
                var resp = new HttpResponseMessage(_postStatus);
                if (RespondJson is not null) resp.Content = new System.Net.Http.StringContent(RespondJson, System.Text.Encoding.UTF8, "application/json");
                return resp;
            }));
        }

        public Task<int> HandleAsync(string stdin, long processStart = 0) =>
            ClaudeHookCommand.HandleCore(Client, AuthStatus.Ok, Spool, processStart == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : processStart,
                "http://localhost", new StringReader(stdin),
                memoryStoreFactory: () => new SessionStartMemoryLeaseStore(Path.Combine(_tmpHome, "memory")));

        public IEnumerable<string> SpoolFiles =>
            Directory.Exists(_spoolPath) ? Directory.EnumerateFiles(_spoolPath) : [];

        /// <summary>Drops a ".ordered-*" temp (the ordered drain's withheld-remainder namespace)
        /// straight into the spool dir, simulating a session-end held back by a prior ordered pass.</summary>
        public void WriteOrderedTemp(string sid, string jsonLine) {
            Directory.CreateDirectory(_spoolPath);
            File.WriteAllText(Path.Combine(_spoolPath, $"{sid}.ordered-1-1"), jsonLine + "\n");
        }

        public void Dispose() { Client.Dispose(); try { Directory.Delete(_tmpHome, true); } catch { } }
    }

    sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => impl(r, ct);
    }
}
