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
        var forceRefreshValues = new List<bool>();
        var refreshing = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null), (forceRefresh, _) => {
            forceRefreshValues.Add(forceRefresh);
            calls++;
            var status = calls == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.NoContent;
            return Task.FromResult(new HttpClient(new StaticHandler(status, "")));
        }, disposeClients: true);
        var request = new SessionStartMemoryContextRequest(
            "https://example.test", null, false, TimeSpan.FromSeconds(1), CancellationToken.None);

        var healed = await refreshing.GetAsync(request);
        var redirected = await new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
            (_, _) => Task.FromResult(new HttpClient(new StaticHandler(HttpStatusCode.Redirect, ""))))
            .GetAsync(request);

        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(forceRefreshValues).IsEquivalentTo([false, true]);
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
