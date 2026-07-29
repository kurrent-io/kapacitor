using System.Net;
using System.Text;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class SessionStartMemoryFoundationTests {
    [Test]
    public async Task Canonical_key_distinguishes_absent_token_from_literal_text() {
        var absent = SessionStartMemoryIdentity.Create(SessionStartHarness.Claude, "session", null);
        var literal = SessionStartMemoryIdentity.Create(SessionStartHarness.Claude, "session", "native-session");

        await Assert.That(absent).IsNotEqualTo(literal);
        await Assert.That(absent).Matches("^[0-9a-f]{64}$");
    }

    [Test]
    public async Task Canonical_key_is_length_delimited_and_lifecycle_scoped() {
        var left = SessionStartMemoryIdentity.Create(SessionStartHarness.Claude, "ab", "c");
        var right = SessionStartMemoryIdentity.Create(SessionStartHarness.Claude, "a", "bc");
        var resumed = SessionStartMemoryIdentity.Create(SessionStartHarness.Claude, "ab", "resume-2");

        await Assert.That(left).IsNotEqualTo(right);
        await Assert.That(left).IsNotEqualTo(resumed);
    }

    [Test]
    public async Task Uuid_harnesses_use_lowercase_N_identity() {
        var id = SessionStartMemoryIdentity.NormalizeSessionId(
            SessionStartHarness.Cursor, "A0D44A4A-5059-4D1F-9C93-2A1ADCE89C2E");

        await Assert.That(id).IsEqualTo("a0d44a4a50594d1f9c932a1adce89c2e");
    }

    // Kiro's agentSpawn fires per prompt, so an id spelled differently between firings would mean two
    // lease keys and a re-injected index. Non-GUID ids must still be accepted (the dispatcher's id is
    // whatever Kiro sends), so Kiro shares Claude's permissive arm rather than the fail-closed one.
    [Test]
    public async Task Kiro_uuid_identity_is_canonical_across_spellings_but_still_accepts_non_uuids() {
        var dashed    = SessionStartMemoryIdentity.Create(SessionStartHarness.Kiro, "A0D44A4A-5059-4D1F-9C93-2A1ADCE89C2E", null);
        var compact   = SessionStartMemoryIdentity.Create(SessionStartHarness.Kiro, "a0d44a4a50594d1f9c932a1adce89c2e", null);
        var uppercase = SessionStartMemoryIdentity.Create(SessionStartHarness.Kiro, "A0D44A4A50594D1F9C932A1ADCE89C2E", null);

        await Assert.That(compact).IsEqualTo(dashed);
        await Assert.That(uppercase).IsEqualTo(dashed);

        // Not fail-closed: an id that is not a GUID still yields a usable identity.
        await Assert.That(SessionStartMemoryIdentity.NormalizeSessionId(SessionStartHarness.Kiro, "kiro-session"))
            .IsEqualTo("kiro-session");
    }

    [Test]
    public async Task Claude_uuid_identity_is_canonical_across_dashed_and_compact_forms() {
        var dashed = SessionStartMemoryIdentity.Create(
            SessionStartHarness.Claude, "A0D44A4A-5059-4D1F-9C93-2A1ADCE89C2E", null);
        var compact = SessionStartMemoryIdentity.Create(
            SessionStartHarness.Claude, "a0d44a4a50594d1f9c932a1adce89c2e", null);

        await Assert.That(dashed).IsEqualTo(compact);
    }

    [Test]
    public async Task Lifecycle_policy_does_not_poison_unknown_or_subagent_callbacks() {
        var unknown = SessionStartMemoryLifecyclePolicy.Decide(new(
            SessionStartHarness.Kiro, "s", null, true, false,
            SessionLifecycleReason.Unknown, CallbackMayRepeat: true));
        var subagent = SessionStartMemoryLifecyclePolicy.Decide(new(
            SessionStartHarness.Kiro, "s", null, false, true,
            SessionLifecycleReason.New, CallbackMayRepeat: true));
        var top = SessionStartMemoryLifecyclePolicy.Decide(new(
            SessionStartHarness.Kiro, "s", null, true, true,
            SessionLifecycleReason.New, CallbackMayRepeat: true));

        await Assert.That(unknown).IsEqualTo(SessionMemoryLifecycleDecision.RetryLaterNoCommit);
        await Assert.That(subagent).IsEqualTo(SessionMemoryLifecycleDecision.IneligibleNoCommit);
        await Assert.That(top).IsEqualTo(SessionMemoryLifecycleDecision.EligibleWithLease);
    }

    [Test]
    public async Task Compact_is_ineligible_in_v1() {
        var decision = SessionStartMemoryLifecyclePolicy.Decide(new(
            SessionStartHarness.Claude, "s", null, true, true,
            SessionLifecycleReason.Compact, CallbackMayRepeat: false));

        await Assert.That(decision).IsEqualTo(SessionMemoryLifecycleDecision.IneligibleNoCommit);
    }

    [Test]
    public async Task Authoritative_top_level_repeated_callback_uses_the_lease_store() {
        var decision = SessionStartMemoryLifecyclePolicy.Decide(new(
            SessionStartHarness.Kiro, "session", null, true, true,
            SessionLifecycleReason.RepeatedTurnCallback, CallbackMayRepeat: true));

        await Assert.That(decision).IsEqualTo(SessionMemoryLifecycleDecision.EligibleWithLease);
    }

    [Test]
    public async Task Typed_emitter_adds_marker_groups_and_never_accepts_bodies() {
        var entries = new[] {
            new SessionStartMemoryEntry("1", "org-rule", "org", "fact", "feedback"),
            new SessionStartMemoryEntry("2", "mine", "user", "my fact", "preference")
        };

        var fragment = MemoryIndexEmitter.BuildFragment(entries);

        await Assert.That(fragment).StartsWith("<!-- kcap-memory-index:v1 -->\n## Team memory");
        await Assert.That(fragment).Contains("### Org\n- org-rule: fact");
        await Assert.That(fragment).Contains("### Yours\n- mine: my fact");
        await Assert.That(Encoding.UTF8.GetByteCount(fragment!)).IsLessThanOrEqualTo(24 * 1024);
    }

    [Test]
    public async Task Output_adapters_match_exact_golden_bytes() {
        const string fragment = "F";

        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Claude, fragment))
            .IsEqualTo("{\"hookSpecificOutput\":{\"hookEventName\":\"SessionStart\",\"additionalContext\":\"F\"}}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Claude, null)).IsEqualTo("");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Codex, fragment))
            .IsEqualTo("{\"continue\":true,\"hookSpecificOutput\":{\"hookEventName\":\"SessionStart\",\"additionalContext\":\"F\"}}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Cursor, null)).IsEqualTo("{}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Cursor, fragment))
            .IsEqualTo("{\"additional_context\":\"F\"}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Copilot, fragment))
            .IsEqualTo("{\"additionalContext\":\"F\"}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Copilot, null)).IsEqualTo("{}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Gemini, fragment))
            .IsEqualTo("{\"hookSpecificOutput\":{\"hookEventName\":\"SessionStart\",\"additionalContext\":\"F\"}}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Gemini, null)).IsEqualTo("{}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Kiro, fragment)).IsEqualTo("F\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Kiro, null)).IsEqualTo("");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Pi, fragment)).IsEqualTo("F\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.OpenCode, null)).IsEqualTo("");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Antigravity, fragment))
            .IsEqualTo("{\"injectSteps\":[{\"userMessage\":\"F\"}]}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Antigravity, null)).IsEqualTo("{}\n");
    }

    [Test]
    public async Task Extension_state_is_first_nonempty_wins_and_delivers_once() {
        var state = new SessionStartMemoryExtensionState();
        await state.ObserveBridgeResultAsync("key", "first");
        await state.ObserveBridgeResultAsync("key", "second");
        await state.ObserveBridgeResultAsync("key", null);

        await Assert.That(await state.TakeForDeliveryAsync("key")).IsEqualTo("first");
        await Assert.That(await state.TakeForDeliveryAsync("key")).IsNull();
    }

    [Test]
    public async Task Lease_store_has_one_winner_and_fences_stale_owner() {
        var root = TempDir();
        try {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
            var store = new SessionStartMemoryLeaseStore(root, time);
            var first = await store.TryBeginAsync(new string('a', 64), TimeSpan.FromSeconds(1));
            var blocked = await store.TryBeginAsync(new string('a', 64), TimeSpan.FromSeconds(1));
            await Assert.That(first).IsNotNull();
            await Assert.That(blocked).IsNull();

            time.Advance(TimeSpan.FromSeconds(31));
            var replacement = await store.TryBeginAsync(new string('a', 64), TimeSpan.FromSeconds(1));
            await Assert.That(replacement).IsNotNull();
            await Assert.That(await store.CompleteAsync(first!, SessionStartMemoryDisposition.Ready, TimeSpan.FromSeconds(1))).IsFalse();
            await Assert.That(await store.CompleteAsync(replacement!, SessionStartMemoryDisposition.Ready, TimeSpan.FromSeconds(1))).IsTrue();
            await Assert.That(await store.TryBeginAsync(new string('a', 64), TimeSpan.FromSeconds(1))).IsNull();
        } finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Concurrent_lease_attempts_have_exactly_one_winner() {
        var root = TempDir();
        try {
            var key = new string('d', 64);
            var attempts = Enumerable.Range(0, 16)
                .Select(_ => new SessionStartMemoryLeaseStore(root).TryBeginAsync(key, TimeSpan.FromSeconds(2)));
            var winners = (await Task.WhenAll(attempts)).Count(static lease => lease is not null);

            await Assert.That(winners).IsEqualTo(1);
        } finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Completion_guarantee_expires_at_thirty_day_sweep_boundary() {
        var root = TempDir();
        try {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
            var store = new SessionStartMemoryLeaseStore(root, time);
            var key = new string('e', 64);
            var lease = await store.TryBeginAsync(key, TimeSpan.FromSeconds(1));
            await store.CompleteAsync(lease!, SessionStartMemoryDisposition.Ready, TimeSpan.FromSeconds(1));

            time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromTicks(1));
            await Assert.That(await store.TryBeginAsync(key, TimeSpan.FromSeconds(1))).IsNull();
            time.Advance(TimeSpan.FromTicks(1));
            await store.SweepAsync(TimeSpan.FromSeconds(1));
            await Assert.That(await store.TryBeginAsync(key, TimeSpan.FromSeconds(1))).IsNotNull();
        } finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Sweep_advances_past_poison_record() {
        var root = TempDir();
        try {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
            var store = new SessionStartMemoryLeaseStore(root, time);
            foreach (var key in new[] { new string('a', 64), new string('c', 64) }) {
                var lease = await store.TryBeginAsync(key, TimeSpan.FromSeconds(1));
                await store.CompleteAsync(lease!, SessionStartMemoryDisposition.Ready, TimeSpan.FromSeconds(1));
            }
            var poison = Path.Combine(root, new string('b', 64) + ".json");
            await File.WriteAllTextAsync(poison, "not-json");
            time.Advance(TimeSpan.FromDays(30));

            await store.SweepAsync(TimeSpan.FromSeconds(1));

            await Assert.That(File.Exists(Path.Combine(root, new string('a', 64) + ".json"))).IsFalse();
            await Assert.That(File.Exists(poison)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(root, new string('c', 64) + ".json"))).IsFalse();
        } finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Retry_pending_obeys_cooldown_and_then_heals() {
        var root = TempDir();
        try {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
            var store = new SessionStartMemoryLeaseStore(root, time);
            var key = new string('b', 64);
            var lease = await store.TryBeginAsync(key, TimeSpan.FromSeconds(1));
            await Assert.That(await store.RetryAsync(lease!, null, TimeSpan.FromSeconds(1))).IsTrue();
            await Assert.That(await store.TryBeginAsync(key, TimeSpan.FromSeconds(1))).IsNull();

            time.Advance(TimeSpan.FromSeconds(5));
            await Assert.That(await store.TryBeginAsync(key, TimeSpan.FromSeconds(1))).IsNotNull();
        } finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Provider_maps_empty_malformed_and_ready_responses() {
        var scope = new FixedScopeResolver("repo", "machine");
        var empty = new SessionStartMemoryContextProvider(scope,
            (_, _) => Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.OK, "[]"))));
        var malformed = new SessionStartMemoryContextProvider(scope,
            (_, _) => Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.OK, "[{}]"))));
        var ready = new SessionStartMemoryContextProvider(scope,
            (_, _) => Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.OK,
                "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]"))));
        var request = new SessionStartMemoryContextRequest("https://example", "/repo", false,
            TimeSpan.FromSeconds(1), CancellationToken.None);

        await Assert.That((await empty.GetAsync(request)).Disposition)
            .IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
        await Assert.That((await malformed.GetAsync(request)).Disposition)
            .IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
        var result = await ready.GetAsync(request);
        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(result.Fragment).Contains("- s: d");
    }

    [Test]
    public async Task Provider_omits_only_unresolved_scope_axes() {
        var handler = new CapturingHandler(HttpStatusCode.NoContent, "");
        var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, "machine tag"),
            (_, _) => Task.FromResult(new HttpClient(handler)));

        await provider.GetAsync(new SessionStartMemoryContextRequest(
            "https://example.test/", null, false, TimeSpan.FromSeconds(1), CancellationToken.None));

        await Assert.That(handler.Uri).IsEqualTo("https://example.test/api/memories/index?machine=machine%20tag");
    }

    [Test]
    public async Task Provider_refreshes_once_after_401_and_refuses_redirect_status() {
        var calls = 0;
        var rejectedTokens = new List<string?>();
        var refreshing = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null), (rejectedAccessToken, _) => {
            rejectedTokens.Add(rejectedAccessToken);
            calls++;
            var status = calls == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.NoContent;
            var client = new HttpClient(new StaticHandler(status, ""));
            // The real factory attaches a bearer; the retry identifies which token was rejected
            // by reading it back off the client that sent it.
            client.DefaultRequestHeaders.Authorization = new("Bearer", calls == 1 ? "rejected-token" : "fresh-token");
            return Task.FromResult(client);
        }, disposeClients: true);
        var request = new SessionStartMemoryContextRequest(
            "https://example.test", null, false, TimeSpan.FromSeconds(1), CancellationToken.None);

        var healed = await refreshing.GetAsync(request);
        var redirected = await new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
            (_, _) => Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.Redirect, ""))))
            .GetAsync(request);

        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(rejectedTokens).IsEquivalentTo([null, "rejected-token"]);
        await Assert.That(healed.Disposition).IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
        await Assert.That(redirected.Disposition).IsEqualTo(SessionStartMemoryDisposition.RetryableFailure);
    }

    [Test]
    public async Task Orchestrator_returns_ready_fragment_only_to_commit_winner() {
        var root = TempDir();
        try {
            var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
                (_, _) => Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.OK,
                    "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]"))));
            var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root), provider);
            var lifecycle = new SessionMemoryLifecycle(SessionStartHarness.Claude, "session", null,
                true, true, SessionLifecycleReason.New, true);
            var request = new SessionStartMemoryContextRequest(
                "https://example.test", null, false, TimeSpan.FromSeconds(1), CancellationToken.None);

            var first = await orchestrator.GetFragmentAsync(lifecycle, request);
            var repeated = await orchestrator.GetFragmentAsync(lifecycle, request);

            await Assert.That(first).Contains("- s: d");
            await Assert.That(repeated).IsNull();
        } finally { Directory.Delete(root, recursive: true); }
    }

    // A caller can only discover its fragment is undeliverable AFTER the fetch has run (Copilot's
    // lifecycle POST failing permanently means the hook exits non-zero, and Copilot reads hook stdout
    // only on a zero exit). A refused commit must therefore RELEASE the once-per-session lease rather
    // than spend it — proved behaviourally: a later start of the SAME session still gets its fragment,
    // which spending the lease makes permanently impossible.
    //
    // Released means retry_pending with a backoff, not immediately retryable, so the clock is advanced
    // past the store's 1h backoff cap rather than asserting an instant second attempt.
    [Test]
    public async Task A_refused_commit_gate_releases_the_lease_so_a_later_start_still_injects() {
        var root = TempDir();
        try {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));
            var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
                (_, _) => Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.OK,
                    "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]"))));
            var orchestrator = new SessionStartMemoryOrchestrator(
                new SessionStartMemoryLeaseStore(root, time), provider);
            var lifecycle = new SessionMemoryLifecycle(SessionStartHarness.Copilot, "3f2504e0-4f89-41d3-9a0c-0305e82c3301", null,
                true, true, SessionLifecycleReason.New, true);
            var request = new SessionStartMemoryContextRequest(
                "https://example.test", null, false, TimeSpan.FromSeconds(1), CancellationToken.None);

            var refused = await orchestrator.GetFragmentAsync(lifecycle, request, _ => Task.FromResult(false));

            time.Advance(TimeSpan.FromHours(2));

            var retried = await orchestrator.GetFragmentAsync(lifecycle, request, _ => Task.FromResult(true));

            await Assert.That(refused).IsNull();
            await Assert.That(retried).Contains("- s: d");
        } finally { Directory.Delete(root, recursive: true); }
    }

    // The gate must not become a second way to lose the fragment: granted behaves exactly as the
    // ungated path, including still being once-per-session.
    [Test]
    public async Task A_granted_commit_gate_commits_the_lease_exactly_as_the_ungated_path() {
        var root = TempDir();
        try {
            var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
                (_, _) => Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.OK,
                    "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]"))));
            var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root), provider);
            var lifecycle = new SessionMemoryLifecycle(SessionStartHarness.Copilot, "3f2504e0-4f89-41d3-9a0c-0305e82c3301", null,
                true, true, SessionLifecycleReason.New, true);
            var request = new SessionStartMemoryContextRequest(
                "https://example.test", null, false, TimeSpan.FromSeconds(1), CancellationToken.None);

            var first = await orchestrator.GetFragmentAsync(lifecycle, request, _ => Task.FromResult(true));
            var repeated = await orchestrator.GetFragmentAsync(lifecycle, request, _ => Task.FromResult(true));

            await Assert.That(first).Contains("- s: d");
            await Assert.That(repeated).IsNull();
        } finally { Directory.Delete(root, recursive: true); }
    }

    const string OneMemoryJson =
        "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]";

    // Exactly what KiroHookCommand builds: agentSpawn fires per PROMPT, so the callback repeats and
    // the lease is the only thing preventing re-injection.
    static SessionMemoryLifecycle KiroLifecycle(string sessionId) =>
        new(SessionStartHarness.Kiro, sessionId, LifecycleInstanceId: null,
            IsTopLevel: true, ClassificationAuthoritative: true,
            SessionLifecycleReason.RepeatedTurnCallback, CallbackMayRepeat: true);

    static SessionStartMemoryContextRequest KiroRequest(double seconds = 1) =>
        new("https://example.test", null, false, TimeSpan.FromSeconds(seconds), CancellationToken.None);

    // THE Kiro acceptance criterion. Kiro has no once-per-session hook: agentSpawn fires on every
    // prompt with the same session id. Without the lease the index would be re-injected — and
    // re-charged — every turn, and would steadily bias the conversation.
    [Test]
    public async Task Kiro_repeated_agent_spawn_injects_once_then_yields_nothing() {
        var root = TempDir();
        try {
            var calls = 0;
            var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
                (_, _) => {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.OK, OneMemoryJson)));
                });
            var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root), provider);

            var first  = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());
            var second = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());
            var third  = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());

            await Assert.That(first).Contains("- s: d");
            await Assert.That(second).IsNull();
            await Assert.That(third).IsNull();

            // Not merely "no output" — no repeat FETCH either, or every prompt would still pay the call.
            await Assert.That(calls).IsEqualTo(1);
        } finally { Directory.Delete(root, recursive: true); }
    }

    // A genuinely new Kiro session brings a new session id, hence a new lease key. No Kiro-specific
    // "is this new?" logic exists or should: identity is the whole mechanism.
    [Test]
    public async Task Kiro_distinct_session_ids_inject_independently() {
        var root = TempDir();
        try {
            var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
                (_, _) => Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.OK, OneMemoryJson))));
            var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root), provider);

            var a = await orchestrator.GetFragmentAsync(KiroLifecycle("session-a"), KiroRequest());
            var b = await orchestrator.GetFragmentAsync(KiroLifecycle("session-b"), KiroRequest());

            await Assert.That(a).Contains("- s: d");
            await Assert.That(b).Contains("- s: d");
        } finally { Directory.Delete(root, recursive: true); }
    }

    // A transient server failure must NOT burn the session's one injection — a later prompt's
    // agentSpawn recovers it. Released means retry_pending behind a backoff, so the clock is advanced
    // past the store's 1h cap rather than asserting an instant retry.
    [Test]
    public async Task Kiro_retryable_failure_lets_a_later_prompt_still_inject() {
        var root = TempDir();
        try {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));
            var calls = 0;
            var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
                (_, _) => Task.FromResult(new HttpClient(new StaticHandler(
                    Interlocked.Increment(ref calls) == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK,
                    OneMemoryJson))));
            var orchestrator = new SessionStartMemoryOrchestrator(
                new SessionStartMemoryLeaseStore(root, time), provider);

            var failed = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());

            time.Advance(TimeSpan.FromHours(2));

            var recovered = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());

            await Assert.That(failed).IsNull();
            await Assert.That(recovered).Contains("- s: d");
        } finally { Directory.Delete(root, recursive: true); }
    }

    // A successful-but-empty index must still COMMIT, or a team with no memories yet would re-fetch on
    // every single Kiro prompt forever.
    [Test]
    public async Task Kiro_a_successful_empty_index_still_suppresses_later_prompts() {
        var root = TempDir();
        try {
            var calls = 0;
            var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
                (_, _) => {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.NoContent, "")));
                });
            var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root), provider);

            await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());
            var second = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());

            await Assert.That(second).IsNull();
            await Assert.That(calls).IsEqualTo(1);
        } finally { Directory.Delete(root, recursive: true); }
    }

    // A losing agentSpawn callback must be fenced by a lease that is genuinely HELD — not merely
    // already-completed. This is ordered deterministically rather than raced, because a race is
    // exactly what cannot be asserted: an all-synchronous provider lets the winner commit before the
    // next caller is even constructed, and counting "how many callers started" proves nothing about
    // whether any of them reached the lease.
    //
    // So: start the winner, wait until its provider signals from INSIDE the fetch (at which point the
    // lease is provably held), run the losers to completion against that held lease, and only then
    // release the winner. No timeout participates in the passing path.
    [Test]
    public async Task Kiro_agent_spawns_arriving_while_the_lease_is_held_are_fenced_out() {
        var root = TempDir();
        try {
            var fetches       = 0;
            var winnerHolding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWinner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
                async (_, ct) => {
                    Interlocked.Increment(ref fetches);
                    winnerHolding.TrySetResult();
                    // Held until the losers have been through. The timeout is a suite-safety net only:
                    // it is never reached on the passing path, and reaching it fails the test anyway
                    // (the losers would no longer be contending for a held lease).
                    await releaseWinner.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

                    return new HttpClient(new StaticHandler(HttpStatusCode.OK, OneMemoryJson));
                });
            var store = new SessionStartMemoryLeaseStore(root);

            var winner = Task.Run(() => new SessionStartMemoryOrchestrator(store, provider)
                .GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest(20)));

            // The winner is now inside its fetch, holding the lease.
            await winnerHolding.Task;

            var losers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                new SessionStartMemoryOrchestrator(store, provider)
                    .GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest(20))));

            // The winner is provably STILL inside its fetch, so the lease was genuinely held for the
            // whole of the losers' run — this is what separates the test from the sequential case.
            await Assert.That(winner.IsCompleted).IsFalse();

            // Every loser was refused while that lease was held — and none of them fetched.
            await Assert.That(losers.All(r => r is null)).IsTrue();
            await Assert.That(fetches).IsEqualTo(1);

            releaseWinner.TrySetResult();

            await Assert.That(await winner).Contains("- s: d");
            await Assert.That(fetches).IsEqualTo(1);
        } finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Disabled_request_does_not_fetch_or_write_a_lease_record() {
        var root = TempDir();
        try {
            var clientCalls = 0;
            var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null), (_, _) => {
                clientCalls++;
                return Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.NoContent, "")));
            });
            var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root), provider);
            var lifecycle = new SessionMemoryLifecycle(SessionStartHarness.Claude, "session", null,
                true, true, SessionLifecycleReason.New, false);

            var fragment = await orchestrator.GetFragmentAsync(lifecycle,
                new SessionStartMemoryContextRequest(
                    "https://example.test", null, true, TimeSpan.FromSeconds(1), CancellationToken.None));

            await Assert.That(fragment).IsNull();
            await Assert.That(clientCalls).IsEqualTo(0);
            await Assert.That(Directory.EnumerateFiles(root)).IsEmpty();
        } finally { Directory.Delete(root, recursive: true); }
    }

    static string TempDir() {
        var path = Path.Combine(Path.GetTempPath(), "kcap-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider {
        DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    sealed class FixedScopeResolver(string? repo, string? machine) : ISessionStartMemoryScopeResolver {
        public Task<SessionStartMemoryScope> ResolveAsync(string? cwd, TimeSpan budget, CancellationToken ct) =>
            Task.FromResult(new SessionStartMemoryScope(repo, machine));
    }

    sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler {
        public string? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Uri = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
