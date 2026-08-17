using System.Text.Json;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class LaunchConsentGateTests {
    static (LaunchConsentGate gate, LaunchConsentStore store, string dir) Build(
        TempDir tmp,
        LaunchConsentDefault def = LaunchConsentDefault.Allow, ILaunchConsentPrompter? prompter = null,
        TimeProvider? time = null, int promptTimeoutSeconds = 5) {
        var dir = tmp.CreateDir(Guid.NewGuid().ToString("N")[..8]);
        var store = new LaunchConsentStore(dir, NullLogger.Instance);
        store.TryReplace(new LaunchConsentPolicy(def, promptTimeoutSeconds, []), out _);
        var log = new LaunchConsentDecisionLog(dir, NullLogger.Instance);
        var gate = new LaunchConsentGate(store, log, prompter, time ?? TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);
        return (gate, store, dir);
    }

    static LaunchConsentInput Input(bool owner = false, string? requesterDisplay = null) =>
        new("user_x", owner, "agent", "/tmp/repo", "claude", requesterDisplay);

    sealed class FakePrompter(bool? answer, bool hasSubscriber = true) : ILaunchConsentPrompter {
        public LaunchConsentPromptRequest? Seen;
        public bool HasSubscriber => hasSubscriber;
        public Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct) =>
            Task.FromResult(hasSubscriber);
        public Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, TimeProvider time, CancellationToken ct) {
            Seen = req;
            return Task.FromResult(answer);
        }
    }

    /// Like FakePrompter, but accumulates every request it is asked to prompt — for pinning
    /// per-call identity (e.g. distinct minted PromptIds across repeated DecideAsync calls).
    sealed class CapturingPrompter(bool? answer) : ILaunchConsentPrompter {
        public readonly List<LaunchConsentPromptRequest> Requests = [];
        public bool HasSubscriber => true;
        public Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct) =>
            Task.FromResult(true);
        public Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, TimeProvider time, CancellationToken ct) {
            Requests.Add(req);
            return Task.FromResult(answer);
        }
    }

    [Test]
    public async Task Allow_default_allows_and_logs() {
        using var tmp = new TempDir();
        var (gate, _, dir) = Build(tmp, LaunchConsentDefault.Allow);
        var o = await gate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(o.Allowed).IsTrue();
        var lines = File.ReadAllLines(Path.Combine(dir, "consent-decisions.jsonl"));
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("\"outcome\":\"allowed\"");
    }

    [Test]
    public async Task Deny_default_denies_with_source_default() {
        using var tmp = new TempDir();
        var (gate, _, _) = Build(tmp, LaunchConsentDefault.Deny);
        var o = await gate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(o.Allowed).IsFalse();
        await Assert.That(o.Source).IsEqualTo("default");
    }

    [Test]
    public async Task Prompt_without_subscriber_denies_no_ui() {
        using var tmp = new TempDir();
        var (gate, _, _) = Build(tmp, LaunchConsentDefault.Prompt, new FakePrompter(true, hasSubscriber: false));
        var o = await gate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(o.Allowed).IsFalse();
        await Assert.That(o.Source).IsEqualTo("prompt_no_ui");
    }

    [Test]
    public async Task Prompt_user_allow_and_deny_and_timeout() {
        using var tmp = new TempDir();
        var (allowGate, _, _) = Build(tmp, LaunchConsentDefault.Prompt, new FakePrompter(true));
        await Assert.That((await allowGate.DecideAsync("a1", Input(), CancellationToken.None)).Allowed).IsTrue();

        var (denyGate, _, _) = Build(tmp, LaunchConsentDefault.Prompt, new FakePrompter(false));
        var denied = await denyGate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(denied.Allowed).IsFalse();
        await Assert.That(denied.Source).IsEqualTo("prompt_user");

        var (timeoutGate, _, _) = Build(tmp, LaunchConsentDefault.Prompt, new FakePrompter(null));
        var timedOut = await timeoutGate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(timedOut.Allowed).IsFalse();
        await Assert.That(timedOut.Source).IsEqualTo("prompt_timeout");
    }

    [Test]
    public async Task Owner_bypasses_deny_default() {
        using var tmp = new TempDir();
        var (gate, _, _) = Build(tmp, LaunchConsentDefault.Deny);
        var o = await gate.DecideAsync("a1", Input(owner: true), CancellationToken.None);
        await Assert.That(o.Allowed).IsTrue();
        await Assert.That(o.Source).IsEqualTo("owner");
    }

    [Test]
    public async Task Denied_reason_prefix_is_the_public_wire_literal() {
        await Assert.That(LaunchConsentGate.DeniedReasonPrefix).IsEqualTo("launch_denied_by_owner");
    }

    [Test]
    public async Task Gate_mints_distinct_prompt_ids_under_a_frozen_clock_and_threads_display() {
        using var tmp = new TempDir();
        // Frozen TimeProvider (never advanced): RequestedAt is identical for both prompts —
        // PromptId must still differ (the failure mode a timestamp identity would have had).
        var prompter = new CapturingPrompter(answer: true);
        var (gate, _, _) = Build(tmp, LaunchConsentDefault.Prompt, prompter, time: new FakeTimeProvider());
        var input = new LaunchConsentInput("github:1", false, "agent", "/r", "codex", "Mathias");

        await gate.DecideAsync("agent-1", input, CancellationToken.None);
        await gate.DecideAsync("agent-1", input, CancellationToken.None);

        var (a, b) = (prompter.Requests[0], prompter.Requests[1]);
        await Assert.That(a.RequestedAt).IsEqualTo(b.RequestedAt);        // clock frozen
        await Assert.That(a.PromptId).IsNotEqualTo(b.PromptId);          // identity is not the clock
        await Assert.That(a.PromptId).IsNotEmpty();
        await Assert.That(a.RequesterDisplay).IsEqualTo("Mathias");
    }

    [Test]
    public async Task Done_records_requester_display_in_the_decision_record() {
        using var tmp = new TempDir();
        // Rule-allowed path (no prompter needed): input display lands in the log record.
        var (gate, _, dir) = Build(tmp, LaunchConsentDefault.Allow);
        var o = await gate.DecideAsync("a1",
            new LaunchConsentInput("github:1", false, "agent", "/r", "codex", "Mathias"), CancellationToken.None);
        await Assert.That(o.Source).IsEqualTo("default");

        var lines = File.ReadAllLines(Path.Combine(dir, "consent-decisions.jsonl"));
        using var parsed = JsonDocument.Parse(lines[0]);
        await Assert.That(parsed.RootElement.GetProperty("requester_display").GetString()).IsEqualTo("Mathias");
    }

    // ══ Deadline discipline (spec §3.2) — grace + monotonic deadline + TimeProvider plumbing.
    // All timing driven by FakeTimeProvider; no real sleeps stand in for timeout semantics. ═══

    /// Drives the two ILaunchConsentPrompter members via caller-supplied callbacks, so a test can
    /// script exactly what the prompter observes/does at each step (including advancing the
    /// FakeTimeProvider mid-callback to simulate elapsed time) without any real concurrency or
    /// real waiting. Also records what the gate passed in, for asserting the computed budgets.
    sealed class TimingPrompter(
        Func<TimeSpan, TimeProvider, CancellationToken, Task<bool>> onWaitForSubscriber,
        Func<LaunchConsentPromptRequest, TimeSpan, TimeProvider, CancellationToken, Task<bool?>> onPrompt)
            : ILaunchConsentPrompter {
        public bool HasSubscriber => false; // unused — DecideAsync no longer short-circuits on this
        public LaunchConsentPromptRequest? SeenRequest;
        public TimeSpan? SeenWait;
        public TimeSpan? SeenPromptTimeout;

        public Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct) {
            SeenWait = wait;
            return onWaitForSubscriber(wait, time, ct);
        }

        public Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, TimeProvider time, CancellationToken ct) {
            SeenRequest = req;
            SeenPromptTimeout = timeout;
            return onPrompt(req, timeout, time, ct);
        }
    }

    /// Wraps a FakeTimeProvider so that the FIRST GetTimestamp() call (the gate's deadline anchor,
    /// `start = time.GetTimestamp()`) returns the pre-jitter instant but then advances the
    /// underlying fake clock by `jitter` as a side effect — simulating real elapsed time (e.g.
    /// scheduling delay) between the deadline being anchored and the very next time-derived
    /// computation (the grace wait's `Remaining()`), without needing a genuine concurrent race.
    sealed class JitterTimeProvider(FakeTimeProvider inner, TimeSpan jitter) : TimeProvider {
        bool _jittered;
        public override long GetTimestamp() {
            var ts = inner.GetTimestamp();
            if (!_jittered) { _jittered = true; inner.Advance(jitter); }
            return ts;
        }
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }

    [Test]
    public async Task Allow_and_Deny_default_never_wait_on_an_unadvanced_time_provider() {
        using var tmp = new TempDir();
        // A FakeTimeProvider that is never Advance()'d would hang forever on any real wait — so
        // completing at all (let alone promptly) proves Allow/Deny never reach the prompt path's
        // clock/wait machinery.
        var time = new FakeTimeProvider();
        var (allowGate, _, _) = Build(tmp, LaunchConsentDefault.Allow, time: time);
        await Assert.That((await allowGate.DecideAsync("a1", Input(), CancellationToken.None)).Allowed).IsTrue();

        var (denyGate, _, _) = Build(tmp, LaunchConsentDefault.Deny, time: time);
        var denied = await denyGate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(denied.Allowed).IsFalse();
    }

    [Test]
    public async Task Prompt_no_subscriber_none_arrives_denies_no_ui_after_grace_and_logs() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider();
        var prompter = new TimingPrompter(
            onWaitForSubscriber: (wait, _, _) => { time.Advance(wait); return Task.FromResult(false); },
            onPrompt: (_, _, _, _) => throw new InvalidOperationException("must not prompt when no subscriber ever arrives"));
        var (gate, _, dir) = Build(tmp, LaunchConsentDefault.Prompt, prompter, time, promptTimeoutSeconds: 20);

        var o = await gate.DecideAsync("a-grace-none", Input(), CancellationToken.None);

        await Assert.That(o.Allowed).IsFalse();
        await Assert.That(o.Source).IsEqualTo("prompt_no_ui");
        await Assert.That(prompter.SeenWait).IsEqualTo(TimeSpan.FromSeconds(5)); // min(5, 20)
        var lines = File.ReadAllLines(Path.Combine(dir, "consent-decisions.jsonl"));
        await Assert.That(lines.Any(l => l.Contains("a-grace-none") && l.Contains("prompt_no_ui"))).IsTrue();
    }

    [Test]
    public async Task Prompt_subscriber_arrives_inside_grace_gets_remaining_budget_and_times_out_after_it() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider();
        var prompter = new TimingPrompter(
            onWaitForSubscriber: (_, _, _) => { time.Advance(TimeSpan.FromSeconds(2)); return Task.FromResult(true); }, // arrives 2s into the grace window
            onPrompt: (_, timeout, _, _) => { time.Advance(timeout); return Task.FromResult<bool?>(null); }); // owner never answers — burns exactly the remaining budget
        var (gate, _, _) = Build(tmp, LaunchConsentDefault.Prompt, prompter, time, promptTimeoutSeconds: 10);

        var before = time.GetUtcNow();
        var o = await gate.DecideAsync("a-grace-arrive", Input(), CancellationToken.None);
        var elapsed = time.GetUtcNow() - before;

        await Assert.That(prompter.SeenWait).IsEqualTo(TimeSpan.FromSeconds(5));           // min(5, 10)
        await Assert.That(prompter.SeenPromptTimeout).IsEqualTo(TimeSpan.FromSeconds(8));  // 10 - 2s elapsed grace
        await Assert.That(o.Allowed).IsFalse();
        await Assert.That(o.Source).IsEqualTo("prompt_timeout");
        await Assert.That(elapsed).IsEqualTo(TimeSpan.FromSeconds(10)); // total wall time never exceeds the policy timeout
    }

    [Test]
    public async Task Time_advanced_between_deadline_anchor_and_grace_wait_shrinks_the_wait_not_the_deadline() {
        using var tmp = new TempDir();
        var fake = new FakeTimeProvider();
        var jittered = new JitterTimeProvider(fake, TimeSpan.FromSeconds(8)); // 8s "elapses" right after the deadline anchor
        var prompter = new TimingPrompter(
            onWaitForSubscriber: (_, _, _) => Task.FromResult(true),
            onPrompt: (_, _, _, _) => Task.FromResult<bool?>(true));
        var (gate, _, _) = Build(tmp, LaunchConsentDefault.Prompt, prompter, jittered, promptTimeoutSeconds: 10);

        var o = await gate.DecideAsync("a-jitter", Input(), CancellationToken.None);

        // grace = min(5,10) = 5, but 8s already "elapsed" (simulated scheduling jitter) by the
        // time the wait is computed, so remaining = 10-8 = 2 < 5 shrinks the wait to 2s. The
        // anchor (`start`) itself never moves: PromptAsync's own budget, recomputed from the SAME
        // anchor, is measured against the identical 8s-elapsed baseline (also 2s) — not reset to
        // a fresh 10s window.
        await Assert.That(prompter.SeenWait).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(prompter.SeenPromptTimeout).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(o.Allowed).IsTrue();
    }

    [Test]
    public async Task Subscriber_arrives_at_or_after_deadline_runs_prompt_with_zero_budget_and_times_out() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider();
        var prompter = new TimingPrompter(
            onWaitForSubscriber: (_, _, _) => { time.Advance(TimeSpan.FromSeconds(10)); return Task.FromResult(true); }, // arrives exactly at the 10s deadline
            onPrompt: (_, _, _, _) => Task.FromResult<bool?>(null));
        var (gate, _, _) = Build(tmp, LaunchConsentDefault.Prompt, prompter, time, promptTimeoutSeconds: 10);

        var o = await gate.DecideAsync("a-zero-budget", Input(), CancellationToken.None);

        await Assert.That(prompter.SeenPromptTimeout).IsEqualTo(TimeSpan.Zero);
        await Assert.That(o.Allowed).IsFalse();
        await Assert.That(o.Source).IsEqualTo("prompt_timeout");
    }

    [Test]
    public async Task RequestedAt_is_anchored_at_gate_entry_not_after_the_grace_wait() {
        using var tmp = new TempDir();
        var time = new FakeTimeProvider();
        var expectedRequestedAt = time.GetUtcNow().ToString("O");
        var prompter = new TimingPrompter(
            onWaitForSubscriber: (_, _, _) => { time.Advance(TimeSpan.FromSeconds(3)); return Task.FromResult(true); }, // grace elapses before arrival
            onPrompt: (_, _, _, _) => Task.FromResult<bool?>(true));
        var (gate, _, _) = Build(tmp, LaunchConsentDefault.Prompt, prompter, time, promptTimeoutSeconds: 10);

        await gate.DecideAsync("a-anchor", Input(), CancellationToken.None);

        await Assert.That(prompter.SeenRequest!.RequestedAt).IsEqualTo(expectedRequestedAt);
    }

    [Test]
    public async Task External_cancellation_during_grace_propagates_without_a_decision_log_record() {
        using var tmp = new TempDir();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var prompter = new TimingPrompter(
            onWaitForSubscriber: (_, _, ct) => throw new OperationCanceledException(ct),
            onPrompt: (_, _, _, _) => throw new InvalidOperationException("must not reach PromptAsync"));
        var (gate, _, dir) = Build(tmp, LaunchConsentDefault.Prompt, prompter, TimeProvider.System, promptTimeoutSeconds: 10);

        await Assert.That(async () => await gate.DecideAsync("a-cancel-grace", Input(), cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(File.Exists(Path.Combine(dir, "consent-decisions.jsonl"))).IsFalse();
    }

    [Test]
    public async Task External_cancellation_during_prompt_propagates_without_a_decision_log_record() {
        using var tmp = new TempDir();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var prompter = new TimingPrompter(
            onWaitForSubscriber: (_, _, _) => Task.FromResult(true),
            onPrompt: (_, _, _, ct) => throw new OperationCanceledException(ct));
        var (gate, _, dir) = Build(tmp, LaunchConsentDefault.Prompt, prompter, TimeProvider.System, promptTimeoutSeconds: 10);

        await Assert.That(async () => await gate.DecideAsync("a-cancel-prompt", Input(), cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(File.Exists(Path.Combine(dir, "consent-decisions.jsonl"))).IsFalse();
    }
}
