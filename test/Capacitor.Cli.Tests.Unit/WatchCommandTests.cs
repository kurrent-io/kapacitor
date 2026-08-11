using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class TryExtractUserTextTests {
    [Test]
    [Arguments("""{"type":"user","message":{"content":"hello world"}}""", "hello world")]
    [Arguments("""{"type":"user","message":{"content":"fix the bug"}}""", "fix the bug")]
    public async Task StringContent_ReturnsText(string line, string expected) {
        var result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task ArrayContent_ReturnsFirstTextElement() {
        const string line   = """{"type":"user","message":{"content":[{"type":"text","text":"from array"}]}}""";
        var          result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("from array");
    }

    [Test]
    public async Task ArrayContent_SkipsNonTextElements() {
        const string line   = """{"type":"user","message":{"content":[{"type":"image","url":"x"},{"type":"text","text":"second"}]}}""";
        var          result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("second");
    }

    [Test]
    [Arguments("""{"type":"assistant","message":{"content":"hi"}}""")]
    [Arguments("""{"type":"system","message":{"content":"hi"}}""")]
    [Arguments("""{"type":"user","isMeta":true,"message":{"content":"meta stuff"}}""")]
    [Arguments("""{"type":"user","message":{"content":"<local-command-stdout>some output"}}""")]
    [Arguments("""{"type":"user","message":{"content":[{"type":"text","text":"<local-command-stdout>output"}]}}""")]
    [Arguments("not json at all")]
    [Arguments("")]
    [Arguments("{}")]
    [Arguments("""{"type":"user"}""")]
    [Arguments("""{"type":"user","message":{}}""")]
    [Arguments("""{"type":"user","message":{"content":[]}}""")]
    public async Task ReturnsNull_ForInvalidOrFilteredInput(string line) {
        var result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsNull();
    }
}

public class StripSystemInstructionsTests {
    [Test]
    [Arguments("Hello <system_instructions>secret stuff</system_instructions> world", "Hello  world")]
    [Arguments("<system-instructions>block</system-instructions>actual prompt", "actual prompt")]
    [Arguments("<system-reminder>reminder content</system-reminder>do the thing", "do the thing")]
    [Arguments("<system_reminder>stuff</system_reminder>real text", "real text")]
    [Arguments("<SYSTEM_INSTRUCTIONS>loud</SYSTEM_INSTRUCTIONS>quiet", "quiet")]
    public async Task Strips_KnownSystemTags(string input, string expected) {
        var result = WatchCommand.StripSystemInstructions(input);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task PreservesText_WithNoSystemTags() {
        var result = WatchCommand.StripSystemInstructions("just a normal prompt");
        await Assert.That(result).IsEqualTo("just a normal prompt");
    }

    [Test]
    public async Task ReturnsNull_WhenOnlySystemInstructions() {
        var result = WatchCommand.StripSystemInstructions("<system_instructions>everything is instructions</system_instructions>");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ReturnsNull_ForNullInput() {
        var result = WatchCommand.StripSystemInstructions(null);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strips_MultipleBlocks() {
        const string input  = "<system_instructions>first</system_instructions>middle<system-reminder>second</system-reminder>end";
        var          result = WatchCommand.StripSystemInstructions(input);
        await Assert.That(result).IsEqualTo("middleend");
    }

    [Test]
    public async Task Strips_MultilineContent() {
        const string input  = "<system_instructions>\nline1\nline2\nline3\n</system_instructions>actual request";
        var          result = WatchCommand.StripSystemInstructions(input);
        await Assert.That(result).IsEqualTo("actual request");
    }

    [Test]
    public async Task CaseInsensitive_MixedCase() {
        var result = WatchCommand.StripSystemInstructions("<System_Instructions>stuff</System_Instructions>prompt");
        await Assert.That(result).IsEqualTo("prompt");
    }
}

public class TryExtractUserTextWithSystemInstructionsTests {
    [Test]
    public async Task Strips_SystemInstructions_FromStringContent() {
        const string line   = """{"type":"user","message":{"content":"<system_instructions>secret</system_instructions>fix the bug"}}""";
        var          result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("fix the bug");
    }

    [Test]
    public async Task ReturnsNull_WhenOnlySystemInstructions_InContent() {
        const string line   = """{"type":"user","message":{"content":"<system_instructions>only instructions here</system_instructions>"}}""";
        var          result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strips_SystemInstructions_FromArrayContent() {
        const string line   = """{"type":"user","message":{"content":[{"type":"text","text":"<system-reminder>reminder</system-reminder>do stuff"}]}}""";
        var          result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("do stuff");
    }
}

public class RepoPayloadChangedTests {
    static RepositoryPayload MakePayload(
            string owner    = "o",
            string repo     = "r",
            string branch   = "main",
            int?   prNumber = 1,
            string prUrl    = "u",
            string prTitle  = "t"
        ) => new() { Owner = owner, RepoName = repo, Branch = branch, PrNumber = prNumber, PrUrl = prUrl, PrTitle = prTitle };

    [Test]
    public async Task NullCurrent_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(null, MakePayload())).IsFalse();

    [Test]
    public async Task NullLastSent_ReturnsTrue() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(MakePayload(), null)).IsTrue();

    [Test]
    public async Task BothNull_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(null, null)).IsFalse();

    [Test]
    public async Task SameValues_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(MakePayload(), MakePayload())).IsFalse();

    [Test]
    [Arguments("Owner")]
    [Arguments("RepoName")]
    [Arguments("Branch")]
    [Arguments("PrNumber")]
    [Arguments("PrUrl")]
    [Arguments("PrTitle")]
    public async Task DifferentField_ReturnsTrue(string field) {
        var a = MakePayload();

        var b = field switch {
            "Owner"    => a with { Owner = "x" },
            "RepoName" => a with { RepoName = "x" },
            "Branch"   => a with { Branch = "x" },
            "PrNumber" => a with { PrNumber = 99 },
            "PrUrl"    => a with { PrUrl = "x" },
            "PrTitle"  => a with { PrTitle = "x" },
            _          => a
        };
        await Assert.That(WatchCommand.RepoPayloadChanged(a, b)).IsTrue();
    }

    [Test]
    public async Task NonComparedFields_DoNotTriggerChange() {
        var a = MakePayload() with { UserName = "alice" };
        var b = MakePayload() with { UserName = "bob" };
        await Assert.That(WatchCommand.RepoPayloadChanged(a, b)).IsFalse();
    }
}

public class CountFileLinesTests {
    [Test]
    [Arguments("line1\nline2\nline3\n", 3)]
    [Arguments("single", 1)]
    [Arguments("", 0)]
    public async Task CountsLines(string content, int expected) {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllTextAsync(path, content);
            await Assert.That(WatchCommand.CountFileLines(path)).IsEqualTo(expected);
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task MissingFile_ReturnsZero() =>
        await Assert.That(WatchCommand.CountFileLines("/tmp/nonexistent_" + Guid.NewGuid())).IsEqualTo(0);
}

public class WatchCommandTests {
    [Test]
    public async Task RunWatch_signature_accepts_vendor_arg() {
        // We can't run a real watcher in a unit test (it'd open SignalR). The
        // hook round-trip integration test exercises the wire path; this guards
        // the signature.
        var method      = typeof(WatchCommand).GetMethod(nameof(WatchCommand.RunWatch))!;
        var vendorParam = method.GetParameters().FirstOrDefault(p => p.Name == "vendor");
        await Assert.That(vendorParam).IsNotNull();
        await Assert.That(vendorParam!.HasDefaultValue).IsTrue();
        await Assert.That(vendorParam.DefaultValue).IsEqualTo("claude");
    }

    [Test]
    [Arguments(null, 60)]      // unset → default 60 min
    [Arguments("", 60)]        // empty → default
    [Arguments("abc", 60)]     // non-numeric → default
    [Arguments("0", 60)]       // non-positive → default (clamped)
    [Arguments("-5", 60)]      // negative → default
    [Arguments("15", 15)]      // valid override
    [Arguments("600", 600)]    // large but allowed
    public async Task ResolveCodexIdleTimeout_parses_env_with_default(string? env, int expectedMinutes) {
        var result = WatchCommand.ResolveCodexIdleTimeout(env);

        await Assert.That(result).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    static readonly DateTimeOffset IdleNow    = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan       IdleWindow = TimeSpan.FromMinutes(60);

    [Test]
    public async Task ShouldEndOnIdle_false_when_disconnected_time_covers_the_overage() {
        // 70 min of wall-clock since last activity, but 15 of those were a SignalR outage. Connected
        // idle = 55 min < 60 min window → must NOT idle-end (a mid-session outage is not idleness).
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(70), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false, disconnectedSinceActivity: TimeSpan.FromMinutes(15));

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_true_when_connected_idle_exceeds_window_despite_prior_outage() {
        // 75 min wall-clock, 10 of them a brief outage → connected idle = 65 min > 60 min → idle-end.
        // (Models repeated reconnects with no new lines still ending after the connected budget.)
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(75), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false, disconnectedSinceActivity: TimeSpan.FromMinutes(10));

        await Assert.That(should).IsTrue();
    }

    [Test]
    public async Task ShouldEndOnIdle_default_disconnected_is_zero_preserving_prior_behavior() {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow);

        await Assert.That(should).IsTrue();
    }

    static readonly TimeSpan RecoveryCeiling = TimeSpan.FromHours(6);

    [Test]
    public async Task DecideParentDeadRecovery_reArms_when_parent_reResolved_alive() {
        // Preferred outcome: re-resolution found the parent and it's alive → re-arm, end nothing —
        // even if the no-progress window already exceeds the ceiling.
        var decision = WatchCommand.DecideParentDeadRecovery(
            reResolvedPid: 4321, isAlive: _ => true,
            noProgressElapsed: RecoveryCeiling + TimeSpan.FromMinutes(1), ceiling: RecoveryCeiling);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentDeadRecovery.ReArm);
    }

    [Test]
    public async Task DecideParentDeadRecovery_endsTerminal_when_resolution_fails_past_ceiling() {
        var decision = WatchCommand.DecideParentDeadRecovery(
            reResolvedPid: null, isAlive: _ => false,
            noProgressElapsed: RecoveryCeiling + TimeSpan.FromMinutes(1), ceiling: RecoveryCeiling);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentDeadRecovery.EndTerminal);
    }

    [Test]
    public async Task DecideParentDeadRecovery_keepsWaiting_below_ceiling_with_no_parent() {
        // A live-but-idle Kiro/OpenCode user parked at a prompt: no parent resolved, but under the
        // ceiling → keep waiting, do NOT end.
        var decision = WatchCommand.DecideParentDeadRecovery(
            reResolvedPid: null, isAlive: _ => false,
            noProgressElapsed: TimeSpan.FromHours(1), ceiling: RecoveryCeiling);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentDeadRecovery.KeepWaiting);
    }

    [Test]
    public async Task DecideParentDeadRecovery_keepsWaiting_when_reResolved_pid_is_dead() {
        // Re-resolution returned a transient/dead pid → not a valid re-arm target; below ceiling.
        var decision = WatchCommand.DecideParentDeadRecovery(
            reResolvedPid: 9, isAlive: _ => false,
            noProgressElapsed: TimeSpan.FromMinutes(5), ceiling: RecoveryCeiling);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentDeadRecovery.KeepWaiting);
    }

    [Test]
    [Arguments(null, 360)]   // unset → default 6h
    [Arguments("", 360)]     // empty → default
    [Arguments("abc", 360)]  // non-numeric → default
    [Arguments("0", 360)]    // non-positive → default
    [Arguments("-5", 360)]   // negative → default
    [Arguments("120", 120)]  // valid override
    public async Task ResolveParentDeadCeiling_parses_env_with_default(string? env, int expectedMinutes) {
        var result = WatchCommand.ResolveParentDeadCeiling(env);

        await Assert.That(result).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Test]
    public async Task ShouldEndOnIdle_true_for_idle_codex_session_watcher() {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsTrue();
    }

    [Test]
    [Arguments("claude")]
    [Arguments("gemini")]
    [Arguments("pi")]
    [Arguments("copilot")]
    [Arguments("kiro")]
    public async Task ShouldEndOnIdle_false_for_non_codex(string vendor) {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: vendor, isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_when_not_yet_idle() {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(59), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_for_subagent_watcher() {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: false, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_below_threshold() {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: false,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_exactly_at_timeout_boundary() {
        // Strictly greater-than: exactly == idleTimeout is NOT yet idle.
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(60), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_when_tool_in_flight_even_if_idle() {
        // A tool call is in progress — must NOT idle-end even after the timeout window.
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: true);

        await Assert.That(should).IsFalse();
    }

    // Task 11 (D1) — Cursor joins the idle-ceiling vendor set (D1/D3): no shell hooks
    // fire per-conversation the way a parent-exit watchdog needs, so an idle transcript (no file
    // growth AND heartbeat gone stale) is the fallback signal a Cursor session has ended. Unlike
    // Codex/Antigravity, the watcher itself must NOT synthesize session-end on this path — end
    // synthesis has exactly one owner (the hook or the server-side sweep) — so the idle-ceiling
    // exit is wired to skip PostSessionEndOnParentExitAsync at the RunWatch call site.
    [Test]
    public async Task Cursor_idle_ceiling_ends_on_idle_without_posting_session_end() {
        var now  = DateTimeOffset.UtcNow;
        var idle = now.AddMinutes(-61);

        await Assert.That(WatchCommand.ShouldEndOnIdle(
            vendor: "cursor", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: idle, now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsTrue();

        await Assert.That(WatchCommand.ShouldEndOnIdle(
            vendor: "cursor", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: now.AddMinutes(-5), now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsFalse();
    }

    // a Cursor CHILD (subagent) watcher never buffers, so
    // WatchState.ThresholdReached never flips true for it; excluding non-session-watchers from
    // the idle ceiling (the pre-fix behavior) made every Cursor child watcher permanently
    // ineligible to idle-exit. The exemption is Cursor-only.
    [Test]
    public async Task Cursor_child_watcher_is_idle_ceiling_eligible_without_threshold_reached() {
        var now  = DateTimeOffset.UtcNow;
        var idle = now.AddMinutes(-61);

        await Assert.That(WatchCommand.ShouldEndOnIdle(
            vendor: "cursor", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: idle, now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsTrue();

        // Not yet idle — still false.
        await Assert.That(WatchCommand.ShouldEndOnIdle(
            vendor: "cursor", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now.AddMinutes(-5), now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsFalse();
    }

    // Regression guard: the child-watcher exemption from the threshold gate is Cursor-specific —
    // a non-session (subagent) watcher for every other idle-ceiling vendor (codex/antigravity)
    // must stay ineligible, exactly as before this fix.
    [Test]
    [Arguments("codex")]
    [Arguments("antigravity")]
    public async Task NonCursor_child_watcher_stays_ineligible_for_the_idle_ceiling(string vendor) {
        var now  = DateTimeOffset.UtcNow;
        var idle = now.AddMinutes(-61);

        await Assert.That(WatchCommand.ShouldEndOnIdle(
            vendor: vendor, isSessionWatcher: false, thresholdReached: true,
            lastActivityAt: idle, now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsFalse();
    }

    // A Claude CHILD (subagent) watcher's only exit paths were the SubagentStop-driven
    // StopWatcher signal and the parent-exit watchdog, so a missed SubagentStop leaked the watcher
    // for the whole life of the parent session (observed: 8 watchers surviving 1-12 days against a
    // live parent). Child watchers join the ceiling; like Cursor's, they never buffer, so the
    // threshold gate cannot apply.
    [Test]
    public async Task Claude_child_watcher_is_idle_ceiling_eligible_without_threshold_reached() {
        var now = DateTimeOffset.UtcNow;

        await Assert.That(WatchCommand.ShouldEndOnIdle(
            vendor: "claude", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now.AddMinutes(-61), now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsTrue();

        await Assert.That(WatchCommand.ShouldEndOnIdle(
            vendor: "claude", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now.AddMinutes(-5), now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsFalse();
    }

    // The whole reason the ceiling is safe: a subagent running a long tool call writes nothing to
    // its transcript between the tool_use and its tool_result, so transcript silence alone must
    // never end it.
    [Test]
    public async Task Claude_child_watcher_never_idle_exits_while_a_tool_is_in_flight() {
        var now = DateTimeOffset.UtcNow;

        await Assert.That(WatchCommand.ShouldEndOnIdle(
            vendor: "claude", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now.AddMinutes(-61), now: now, idleTimeout: TimeSpan.FromMinutes(60),
            toolInFlight: true)).IsFalse();
    }

    // Regression guard: the ceiling is scoped to CHILD watchers. A Claude SESSION watcher has a
    // working parent-exit watchdog and a sessionEnd hook, so it must stay ineligible — otherwise a
    // user idling in a live Claude session would have their session ended out from under them.
    [Test]
    public async Task Claude_session_watcher_stays_ineligible_for_the_idle_ceiling() {
        var now = DateTimeOffset.UtcNow;

        await Assert.That(WatchCommand.ShouldEndOnIdle(
            vendor: "claude", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: now.AddMinutes(-61), now: now, idleTimeout: TimeSpan.FromMinutes(60))).IsFalse();
    }

    // The tool tracker is what stops the ceiling reaping a LIVE subagent that is quiet because a
    // build/test run is in flight. If this gate ever stops matching the watchers the ceiling
    // applies to, PendingClaudeToolCalls stays empty, toolInFlight is permanently false, and the
    // suppression silently disappears — so the gate is a predicate rather than an inline
    // condition buried in the drain loop.
    [Test]
    [Arguments("claude", false, true)]   // child watcher: the only one with a ceiling, so the only one that needs the guard
    [Arguments("claude", true,  false)]  // session watcher: no ceiling, so parsing every line would be pure waste
    [Arguments("codex",  false, false)]  // other vendors have their own trackers
    [Arguments("cursor", false, false)]
    public async Task TracksClaudeToolCalls_matches_the_watchers_the_ceiling_applies_to(
            string vendor, bool isSessionWatcher, bool expected) {
        await Assert.That(WatchCommand.TracksClaudeToolCalls(vendor, isSessionWatcher)).IsEqualTo(expected);
    }

    // Guard on the composed policy at its REAL default, not a test-supplied window: a Claude
    // subagent goes quiet for six hours before it is reaped, and never while a tool is in flight.
    [Test]
    public async Task Claude_subagent_ceiling_composes_to_six_hours_and_yields_to_tools_in_flight() {
        var now    = DateTimeOffset.UtcNow;
        var window = WatchCommand.ResolveIdleWindow("claude", isSessionWatcher: false, _ => null);

        bool ExitsAfter(TimeSpan quiet, bool toolInFlight = false) => WatchCommand.ShouldEndOnIdle(
            vendor: "claude", isSessionWatcher: false, thresholdReached: false,
            lastActivityAt: now - quiet, now: now, idleTimeout: window, toolInFlight: toolInFlight);

        await Assert.That(ExitsAfter(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1))).IsTrue();
        await Assert.That(ExitsAfter(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59))).IsFalse();

        // A 12-hour build still holds the watcher open — transcript silence alone never reaps.
        await Assert.That(ExitsAfter(TimeSpan.FromHours(12), toolInFlight: true)).IsFalse();
    }

    // The ceiling only reaps anything if RunWatch actually hands a Claude CHILD watcher the
    // subagent window rather than the 60-minute Codex default the `_` branch used to give it.
    [Test]
    public async Task ResolveIdleWindow_gives_claude_child_watchers_the_subagent_ceiling() {
        var window = WatchCommand.ResolveIdleWindow("claude", isSessionWatcher: false, _ => null);

        await Assert.That(window).IsEqualTo(WatchCommand.DefaultClaudeSubagentIdleCeiling);
    }

    [Test]
    public async Task ResolveIdleWindow_honours_the_claude_subagent_env_override() {
        var window = WatchCommand.ResolveIdleWindow(
            "claude", isSessionWatcher: false,
            name => name == "KCAP_CLAUDE_SUBAGENT_IDLE_MINUTES" ? "90" : null);

        await Assert.That(window).IsEqualTo(TimeSpan.FromMinutes(90));
    }

    // Each vendor keeps reading its OWN knob — a Claude subagent must not be retunable via the
    // Codex knob, and vice versa.
    [Test]
    [Arguments("codex",       true,  "KCAP_CODEX_IDLE_MINUTES",           "15", 15)]
    [Arguments("antigravity", true,  "KCAP_ANTIGRAVITY_IDLE_MINUTES",     "20", 20)]
    [Arguments("cursor",      true,  "KCAP_CURSOR_IDLE_CEILING_MINUTES",  "25", 25)]
    [Arguments("claude",      false, "KCAP_CLAUDE_SUBAGENT_IDLE_MINUTES", "30", 30)]
    public async Task ResolveIdleWindow_reads_the_vendors_own_knob(
            string vendor, bool isSessionWatcher, string knob, string value, int expectedMinutes) {
        var window = WatchCommand.ResolveIdleWindow(
            vendor, isSessionWatcher, name => name == knob ? value : null);

        await Assert.That(window).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Test]
    [Arguments(null, 360)]    // unset → default 6h
    [Arguments("", 360)]      // empty → default
    [Arguments("abc", 360)]   // non-numeric → default
    [Arguments("0", 360)]     // non-positive → default
    [Arguments("-5", 360)]    // negative → default
    [Arguments("90", 90)]     // valid override
    public async Task ResolveClaudeSubagentIdleCeiling_parses_env_with_default(string? env, int expectedMinutes) {
        var result = WatchCommand.ResolveClaudeSubagentIdleCeiling(env);

        await Assert.That(result).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    // ResolveCursorIdleClock: the idle clock is the LATER of transcript
    // activity and the hook heartbeat, for Cursor only.
    [Test]
    public async Task ResolveCursorIdleClock_prefers_the_later_hook_heartbeat_for_cursor() {
        var now             = DateTimeOffset.UtcNow;
        var staleActivity   = now.AddMinutes(-61);
        var freshHeartbeat  = now.AddMinutes(-2); // Cursor still firing hooks recently

        var resolved = WatchCommand.ResolveCursorIdleClock("cursor", staleActivity, freshHeartbeat);

        await Assert.That(resolved).IsEqualTo(freshHeartbeat);
    }

    [Test]
    public async Task ResolveCursorIdleClock_keeps_transcript_activity_when_the_heartbeat_is_older() {
        var now           = DateTimeOffset.UtcNow;
        var freshActivity = now.AddMinutes(-2);
        var staleHeartbeat = now.AddMinutes(-61);

        var resolved = WatchCommand.ResolveCursorIdleClock("cursor", freshActivity, staleHeartbeat);

        await Assert.That(resolved).IsEqualTo(freshActivity);
    }

    [Test]
    public async Task ResolveCursorIdleClock_keeps_transcript_activity_when_no_heartbeat_recorded() {
        var now      = DateTimeOffset.UtcNow;
        var activity = now.AddMinutes(-61);

        var resolved = WatchCommand.ResolveCursorIdleClock("cursor", activity, hookHeartbeatAt: null);

        await Assert.That(resolved).IsEqualTo(activity);
    }

    // Regression guard: for every non-Cursor vendor the heartbeat argument is ignored entirely —
    // the caller never even reads the file for them, but pin the degrade-to-activity behavior
    // defensively in case a heartbeat value is ever passed anyway.
    [Test]
    public async Task ResolveCursorIdleClock_ignores_the_heartbeat_for_non_cursor_vendors() {
        var now      = DateTimeOffset.UtcNow;
        var activity = now.AddMinutes(-61);
        var heartbeat = now; // would win for cursor, must NOT win here

        var resolved = WatchCommand.ResolveCursorIdleClock("codex", activity, heartbeat);

        await Assert.That(resolved).IsEqualTo(activity);
    }

    [Test]
    public async Task Cursor_acked_ack_sets_next_line_cursor() {
        var ack = System.Text.Json.JsonSerializer.Deserialize(
            """{"next_line_number":7}""", CapacitorJsonContext.Default.TranscriptBatchAck);

        await Assert.That(ack.NextLineNumber).IsEqualTo(7);
    }

    // ByteOffsetForAckedLines: maps a server-acked LINE count to the byte
    // offset within the guard's verified range, so the checkpoint advances only as far as the ack
    // actually covers (a partially-disposed D3 "halt-at-the-gap" batch must not have its unacked
    // tail bytes checkpointed as if delivered).
    [Test]
    public async Task ByteOffsetForAckedLines_zero_acked_returns_the_range_start() {
        var range = System.Text.Encoding.UTF8.GetBytes("line0\nline1\nline2\n");

        var offset = WatchCommand.ByteOffsetForAckedLines(range, rangeStartOffset: 100, ackedLineCount: 0);

        await Assert.That(offset).IsEqualTo(100L);
    }

    [Test]
    public async Task ByteOffsetForAckedLines_partial_ack_stops_at_the_nth_newline() {
        var range = System.Text.Encoding.UTF8.GetBytes("line0\nline1\nline2\n"); // 6+6+6 bytes

        // Only the first line acked — offset should land right after its newline (byte 6),
        // relative to the range start.
        var offset = WatchCommand.ByteOffsetForAckedLines(range, rangeStartOffset: 100, ackedLineCount: 1);

        await Assert.That(offset).IsEqualTo(106L);
    }

    [Test]
    public async Task ByteOffsetForAckedLines_two_of_three_acked_stops_after_the_second_newline() {
        var range = System.Text.Encoding.UTF8.GetBytes("line0\nline1\nline2\n"); // 6+6+6 bytes

        var offset = WatchCommand.ByteOffsetForAckedLines(range, rangeStartOffset: 100, ackedLineCount: 2);

        await Assert.That(offset).IsEqualTo(112L);
    }

    [Test]
    public async Task ByteOffsetForAckedLines_full_ack_consumes_the_whole_range() {
        var range = System.Text.Encoding.UTF8.GetBytes("line0\nline1\nline2\n");

        var offset = WatchCommand.ByteOffsetForAckedLines(range, rangeStartOffset: 100, ackedLineCount: 3);

        await Assert.That(offset).IsEqualTo(100L + range.Length);
    }

    [Test]
    public async Task ByteOffsetForAckedLines_acked_more_than_available_newlines_still_bounded_by_the_range() {
        // Defensive: an ack count exceeding what this range accounts for must not read/return
        // past the range's own length.
        var range = System.Text.Encoding.UTF8.GetBytes("line0\nline1\n");

        var offset = WatchCommand.ByteOffsetForAckedLines(range, rangeStartOffset: 0, ackedLineCount: 99);

        await Assert.That(offset).IsEqualTo(range.Length);
    }

    [Test]
    [Arguments(null, 60)]      // unset → default 60 min
    [Arguments("", 60)]        // empty → default
    [Arguments("abc", 60)]     // non-numeric → default
    [Arguments("0", 60)]       // non-positive → default (clamped)
    [Arguments("-5", 60)]      // negative → default
    [Arguments("30", 30)]      // valid override
    public async Task ResolveCursorIdleCeiling_parses_env_with_default(string? env, int expectedMinutes) {
        var result = WatchCommand.ResolveCursorIdleCeiling(env);

        await Assert.That(result).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    // Cursor's own sessionStart hook posts (and spawns this very watcher)
    // before any transcript line is ever read, exactly like Antigravity — so the generic
    // below-threshold buffer must not apply to it either.
    [Test]
    [Arguments("antigravity", true)]
    [Arguments("cursor",      true)]
    [Arguments("codex",       false)]
    [Arguments("claude",      false)]
    [Arguments("gemini",      false)]
    public async Task SkipsThresholdBuffering_matches_only_the_vendors_that_pre_commit_the_session(string vendor, bool expected) =>
        await Assert.That(WatchCommand.SkipsThresholdBuffering(vendor)).IsEqualTo(expected);
}

public class UpdateCodexPendingToolCallsTests {
    [Test]
    public async Task FunctionCall_AddsCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        const string line = """{"type":"response_item","payload":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{}"}}""";

        WatchCommand.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_1")).IsTrue();
    }

    [Test]
    public async Task CustomToolCall_AddsCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        const string line = """{"type":"response_item","payload":{"type":"custom_tool_call","call_id":"call_2","name":"my_tool","arguments":"{}"}}""";

        WatchCommand.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_2")).IsTrue();
    }

    [Test]
    public async Task FunctionCallOutput_RemovesCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "call_1" };
        const string line = """{"type":"response_item","payload":{"type":"function_call_output","call_id":"call_1","output":"done"}}""";

        WatchCommand.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_1")).IsFalse();
    }

    [Test]
    public async Task CustomToolCallOutput_RemovesCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "call_2" };
        const string line = """{"type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"call_2","output":"done"}}""";

        WatchCommand.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_2")).IsFalse();
    }

    [Test]
    public async Task MessageResponseItem_LeavesSetUnchanged() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };
        const string line = """{"type":"response_item","payload":{"type":"message","role":"assistant","content":[]}}""";

        WatchCommand.UpdateCodexPendingToolCalls(pending, line);

        // Set unchanged — still contains "existing", nothing added
        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }

    [Test]
    public async Task MalformedJson_LeavesSetUnchanged_NoThrow() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };

        // Must not throw; set must remain unchanged
        WatchCommand.UpdateCodexPendingToolCalls(pending, "not json at all {{}}");

        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }
}

// Claude's in-flight guard for the subagent idle ceiling. Shapes below are taken verbatim from a
// real subagent transcript: tool_use carries `id`, tool_result carries `tool_use_id`, both inside
// message.content[].
public class UpdateClaudePendingToolCallsTests {
    [Test]
    public async Task ToolUse_AddsId() {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        const string line = """{"isSidechain":true,"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01A","name":"Bash","input":{"command":"ls"}}]}}""";

        WatchCommand.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Contains("toolu_01A")).IsTrue();
    }

    [Test]
    public async Task ToolResult_RemovesId() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "toolu_01A" };
        const string line = """{"isSidechain":true,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01A","type":"tool_result","content":"ok"}]}}""";

        WatchCommand.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Contains("toolu_01A")).IsFalse();
    }

    // One assistant turn can start several tools before any result arrives.
    [Test]
    public async Task MultipleToolUses_InOneMessage_AllTracked() {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        const string line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"working"},{"type":"tool_use","id":"toolu_01A","name":"Bash","input":{}},{"type":"tool_use","id":"toolu_01B","name":"Read","input":{}}]}}""";

        WatchCommand.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Count).IsEqualTo(2);
        await Assert.That(pending.Contains("toolu_01B")).IsTrue();
    }

    [Test]
    public async Task AssistantTextOnly_LeavesSetUnchanged() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };
        const string line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"just thinking"}]}}""";

        WatchCommand.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }

    // Claude Code writes a string content for plain user prompts, not an array.
    [Test]
    public async Task StringContent_LeavesSetUnchanged() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };
        const string line = """{"type":"user","message":{"role":"user","content":"go on then"}}""";

        WatchCommand.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CodexLine_LeavesSetUnchanged() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };
        const string line = """{"type":"response_item","payload":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{}"}}""";

        WatchCommand.UpdateClaudePendingToolCalls(pending, line);

        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }

    [Test]
    public async Task MalformedJson_LeavesSetUnchanged_NoThrow() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };

        WatchCommand.UpdateClaudePendingToolCalls(pending, "not json at all {{}}");

        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }
}

/// <summary>
/// The Codex counterpart of <see cref="ClaudeToolTrackingSourceTests"/>: both watcher state trackers
/// must be fed the drain's RAW lines. RedactLine swaps anything over the size limit for a placeholder
/// with no <c>call_id</c> and a <c>type</c> neither tracker recognises, and an oversized
/// <c>function_call_output</c> — a build log, a big file read — is routine. See issue #528.
/// </summary>
public class CodexToolTrackingSourceTests {
    const string FunctionCall =
        """{"type":"response_item","payload":{"type":"function_call","call_id":"call_big","name":"shell","arguments":"{}"}}""";

    static string OversizedFunctionCallOutput() =>
        "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"call_big\",\"output\":\""
      + new string('x', SecretRedactor.MaxRedactableLineChars + 1024)
      + "\"}}";

    /// <summary>
    /// Worse for Codex than the Claude equivalent was: the idle timeout is the ONLY per-conversation
    /// session-end path (the desktop app's shared app-server never exits), so a stranded call_id
    /// pins toolInFlight true and leaves the session Active in the read model forever.
    /// </summary>
    [Test]
    public async Task RedactedLines_StrandTheCallId_ButRawLinesClearIt() {
        var viaRedacted = new HashSet<string>(StringComparer.Ordinal);
        WatchCommand.UpdateCodexPendingToolCalls(viaRedacted, SecretRedactor.RedactLine(FunctionCall));
        WatchCommand.UpdateCodexPendingToolCalls(viaRedacted, SecretRedactor.RedactLine(OversizedFunctionCallOutput()));

        await Assert.That(viaRedacted.Contains("call_big")).IsTrue();

        var viaRaw = new HashSet<string>(StringComparer.Ordinal);
        WatchCommand.UpdateCodexPendingToolCalls(viaRaw, FunctionCall);
        WatchCommand.UpdateCodexPendingToolCalls(viaRaw, OversizedFunctionCallOutput());

        await Assert.That(viaRaw.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The other direction on the same placeholder: Observe treats any response_item as the turn
    /// re-opening, so an oversized one is not recognised and a child that re-engaged after
    /// task_complete still looks finished — a premature live subagent-stop.
    /// </summary>
    [Test]
    public async Task RedactedResponseItem_FailsToReopenTheTurn_ButTheRawOneDoes() {
        var viaRedacted = new CodexSubagentTurnTracker();
        viaRedacted.Observe("""{"type":"event_msg","payload":{"type":"task_complete"}}""");
        viaRedacted.Observe(SecretRedactor.RedactLine(OversizedFunctionCallOutput()));

        await Assert.That(viaRedacted.TurnCompleted).IsTrue();

        var viaRaw = new CodexSubagentTurnTracker();
        viaRaw.Observe("""{"type":"event_msg","payload":{"type":"task_complete"}}""");
        viaRaw.Observe(OversizedFunctionCallOutput());

        await Assert.That(viaRaw.TurnCompleted).IsFalse();
    }
}

public class ClaudeToolTrackingSourceTests {
    const string ToolUse =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_big","name":"Bash","input":{"command":"build"}}]}}""";

    // Sized off the real threshold so a legitimate change to it can't quietly turn these into
    // tests of the small-line path. The everyday size of a big file read or a build log.
    static string OversizedToolResult() =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"toolu_big\",\"type\":\"tool_result\",\"content\":\""
      + new string('x', SecretRedactor.MaxRedactableLineChars + 1024)
      + "\"}]}}";

    /// <summary>
    /// Why the tracker is fed the drain's RAW lines and not the redacted ones it sends: RedactLine
    /// swaps any line over 64 KiB for a placeholder carrying no tool ids at all. Feed it the
    /// redacted list and an oversized tool_result never clears its id, toolInFlight stays true
    /// forever, and the idle ceiling never fires — the leak this whole feature exists to fix,
    /// silently reinstated on exactly the busiest sessions.
    /// </summary>
    [Test]
    public async Task RedactedLines_StrandThePendingId_ButRawLinesClearIt() {
        var viaRedacted = new HashSet<string>(StringComparer.Ordinal);
        WatchCommand.UpdateClaudePendingToolCalls(viaRedacted, SecretRedactor.RedactLine(ToolUse));
        WatchCommand.UpdateClaudePendingToolCalls(viaRedacted, SecretRedactor.RedactLine(OversizedToolResult()));

        await Assert.That(viaRedacted.Contains("toolu_big")).IsTrue();

        var viaRaw = new HashSet<string>(StringComparer.Ordinal);
        WatchCommand.UpdateClaudePendingToolCalls(viaRaw, ToolUse);
        WatchCommand.UpdateClaudePendingToolCalls(viaRaw, OversizedToolResult());

        await Assert.That(viaRaw.Count).IsEqualTo(0);
    }

    // A watcher that reconnects mid-tool resumes at a server cursor and never re-reads the
    // tool_use that started before it, so without a backfill the pending set comes up empty and a
    // live subagent is eligible for reaping.
    [Test]
    public async Task Backfill_recovers_a_tool_use_left_open_before_the_resume_cursor() {
        var path = Path.Combine(Path.GetTempPath(), $"kcap-backfill-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllLinesAsync(path, [ToolUse, """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"still working"}]}}"""]);

        try {
            var pending = new HashSet<string>(StringComparer.Ordinal);
            await WatchCommand.BackfillClaudePendingToolCallsAsync(pending, path, upToLine: 2, CancellationToken.None);

            await Assert.That(pending.Contains("toolu_big")).IsTrue();
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Backfill_leaves_nothing_pending_when_the_tool_already_completed() {
        var path = Path.Combine(Path.GetTempPath(), $"kcap-backfill-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllLinesAsync(path, [ToolUse, OversizedToolResult()]);

        try {
            var pending = new HashSet<string>(StringComparer.Ordinal);
            await WatchCommand.BackfillClaudePendingToolCallsAsync(pending, path, upToLine: 2, CancellationToken.None);

            await Assert.That(pending.Count).IsEqualTo(0);
        } finally {
            File.Delete(path);
        }
    }

    // Only the lines before the cursor are the watcher's blind spot; everything from the cursor on
    // arrives through the normal drain, and scanning it here would double-apply it.
    [Test]
    public async Task Backfill_stops_at_the_cursor() {
        var path = Path.Combine(Path.GetTempPath(), $"kcap-backfill-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllLinesAsync(path, [ToolUse, OversizedToolResult()]);

        try {
            var pending = new HashSet<string>(StringComparer.Ordinal);
            await WatchCommand.BackfillClaudePendingToolCallsAsync(pending, path, upToLine: 1, CancellationToken.None);

            await Assert.That(pending.Contains("toolu_big")).IsTrue();
        } finally {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The scan is bounded to a window ending at the cursor rather than parsing the whole history:
    /// an unfinished tool is by definition at the tail (nothing is written after its tool_use until
    /// the result arrives), so anything further back is settled and only costs startup latency. It
    /// also means a stale unmatched tool_use from far earlier — an interrupted turn — can't strand
    /// an id and suppress the ceiling forever.
    /// </summary>
    [Test]
    public async Task Backfill_ignores_an_unmatched_tool_use_older_than_the_scan_window() {
        var path = Path.Combine(Path.GetTempPath(), $"kcap-backfill-{Guid.NewGuid():N}.jsonl");

        var lines = new List<string> { ToolUse };  // never resolved, then buried by later activity
        lines.AddRange(Enumerable.Repeat(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"later"}]}}""",
            WatchCommand.ClaudeToolBackfillWindowLines + 10));

        await File.WriteAllLinesAsync(path, lines);

        try {
            var pending = new HashSet<string>(StringComparer.Ordinal);
            await WatchCommand.BackfillClaudePendingToolCallsAsync(pending, path, lines.Count, CancellationToken.None);

            await Assert.That(pending.Count).IsEqualTo(0);
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Backfill_returns_silently_when_already_cancelled() {
        var path = Path.Combine(Path.GetTempPath(), $"kcap-backfill-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllLinesAsync(path, [ToolUse]);

        try {
            var pending = new HashSet<string>(StringComparer.Ordinal);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await WatchCommand.BackfillClaudePendingToolCallsAsync(pending, path, upToLine: 1, cancelled.Token);

            await Assert.That(pending.Count).IsEqualTo(0);
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Backfill_on_a_missing_file_is_a_silent_no_op() {
        var pending = new HashSet<string>(StringComparer.Ordinal);

        await WatchCommand.BackfillClaudePendingToolCallsAsync(
            pending, Path.Combine(Path.GetTempPath(), $"kcap-missing-{Guid.NewGuid():N}.jsonl"), upToLine: 5, CancellationToken.None);

        await Assert.That(pending.Count).IsEqualTo(0);
    }
}

public class CodexTranscriptExtractionTests {
    // Codex wraps every event in a response_item envelope; user prompts are
    // role:"user" message payloads with input_text blocks. See TitleGenerator
    // for the offline-import analog of this extraction.

    [Test]
    public async Task UserText_Extracts_InputText_FromResponseItem() {
        const string line = """
            {"type":"response_item","payload":{"type":"message","role":"user",
             "content":[{"type":"input_text","text":"fix the bug"}]}}
            """;

        var result = WatchCommand.TryExtractUserText(line, "codex");

        await Assert.That(result).IsEqualTo("fix the bug");
    }

    [Test]
    [Arguments("<environment_context>\nworkspace=/tmp\n</environment_context>")]
    [Arguments("# AGENTS.md instructions for /tmp\n\nUse pnpm.")]
    [Arguments("<turn_aborted>user pressed esc</turn_aborted>")]
    public async Task UserText_Skips_CodexInjectedPreludes(string preludeText) {
        var encoded = System.Text.Json.JsonSerializer.Serialize(preludeText);
        var line    = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":" + encoded + "}]}}";

        var result = WatchCommand.TryExtractUserText(line, "codex");

        await Assert.That(result).IsNull();
    }

    [Test]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"reasoning","summary":[]}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[]}}""")]
    [Arguments("""{"type":"user","message":{"content":"claude-shape"}}""")]
    [Arguments("not json")]
    public async Task UserText_ReturnsNull_ForUnrelatedCodexLines(string line) {
        var result = WatchCommand.TryExtractUserText(line, "codex");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task AssistantText_Extracts_OutputText_FromResponseItem() {
        const string line = """
            {"type":"response_item","payload":{"type":"message","role":"assistant",
             "content":[{"type":"output_text","text":"Sure, let me look into that"}]}}
            """;

        var result = WatchCommand.TryExtractAssistantText(line, "codex");

        await Assert.That(result).IsEqualTo("Sure, let me look into that");
    }

    [Test]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"prompt"}]}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"reasoning"}}""")]
    [Arguments("""{"type":"assistant","message":{"content":[{"type":"text","text":"claude-shape"}]}}""")]
    public async Task AssistantText_ReturnsNull_ForUnrelatedCodexLines(string line) {
        var result = WatchCommand.TryExtractAssistantText(line, "codex");

        await Assert.That(result).IsNull();
    }

    [Test]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}}""", true)]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}}""", true)]
    [Arguments("""{"type":"response_item","payload":{"type":"reasoning"}}""", false)]
    [Arguments("""{"type":"response_item","payload":{"type":"function_call"}}""", false)]
    [Arguments("""{"type":"user","message":{"content":"claude shape"}}""", false)]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[]}}""", false)]
    public async Task IsEvent_Codex_OnlyCountsMessagePayloads(string line, bool expected) {
        var result = WatchCommand.IsEvent(line, "codex");

        await Assert.That(result).IsEqualTo(expected);
    }

    // Critical: prelude user-role payloads must NOT count toward the 5-event
    // title threshold. Otherwise a fresh Codex session with a few injected
    // <environment_context>/AGENTS.md entries before any real prompt can trip
    // the threshold and produce a title from prelude content alone.
    [Test]
    [Arguments("<environment_context>\nworkspace=/tmp\n</environment_context>")]
    [Arguments("# AGENTS.md instructions for /tmp\n\nUse pnpm.")]
    [Arguments("<turn_aborted>user pressed esc</turn_aborted>")]
    public async Task IsEvent_Codex_SkipsInjectedUserPreludes(string preludeText) {
        var encoded = System.Text.Json.JsonSerializer.Serialize(preludeText);
        var line    = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":" + encoded + "}]}}";

        var result = WatchCommand.IsEvent(line, "codex");

        await Assert.That(result).IsFalse();
    }
}

// PR #162: Pi emits type:"message" with message.role (not Claude's
// top-level user/assistant), so the watcher title helpers need a Pi branch —
// otherwise live Pi sessions never get the initial/LLM title.
public class PiTitleHelperTests {
    [Test]
    public async Task UserText_StringContent() {
        const string line = """{"type":"message","id":"a1","message":{"role":"user","content":"build the thing"}}""";
        await Assert.That(WatchCommand.TryExtractUserText(line, "pi")).IsEqualTo("build the thing");
    }

    [Test]
    public async Task UserText_ArrayContent_FirstTextBlock_ImagesSkipped() {
        const string line = """{"type":"message","id":"a1","message":{"role":"user","content":[{"type":"image","data":"x"},{"type":"text","text":"look at this"}]}}""";
        await Assert.That(WatchCommand.TryExtractUserText(line, "pi")).IsEqualTo("look at this");
    }

    [Test]
    public async Task AssistantText_FirstTextBlock_ThinkingSkipped() {
        const string line = """{"type":"message","id":"b1","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"on it"}]}}""";
        await Assert.That(WatchCommand.TryExtractAssistantText(line, "pi")).IsEqualTo("on it");
    }

    [Test]
    [Arguments("""{"type":"message","id":"a1","message":{"role":"user","content":"hi"}}""", true)]
    [Arguments("""{"type":"message","id":"b1","message":{"role":"assistant","content":[{"type":"text","text":"ok"}]}}""", true)]
    [Arguments("""{"type":"message","id":"c1","message":{"role":"toolResult","toolCallId":"t1","content":[]}}""", false)]
    [Arguments("""{"type":"session","id":"11111111-2222-3333-4444-555555555555","cwd":"/w"}""", false)]
    [Arguments("""{"type":"model_change","id":"d1","modelId":"gpt-5"}""", false)]
    [Arguments("""{"type":"user","message":{"content":"claude shape"}}""", false)]
    // Empty/contentless Pi user & assistant envelopes must NOT count toward the
    // title-event threshold — they produce no canonical event (mirrors the server
    // normalizer / PiImportSource.IsImportRelevantLine).
    [Arguments("""{"type":"message","id":"e1","message":{"role":"user","content":""}}""", false)]
    [Arguments("""{"type":"message","id":"e2","message":{"role":"user","content":[]}}""", false)]
    [Arguments("""{"type":"message","id":"e3","message":{"role":"user","content":[{"type":"image","data":"x"}]}}""", false)]
    [Arguments("""{"type":"message","id":"e4","message":{"role":"assistant","content":[]}}""", false)]
    // Tool-only assistant turns DO count (NormalizeAssistant emits a tool-call event).
    [Arguments("""{"type":"message","id":"e5","message":{"role":"assistant","content":[{"type":"toolCall","id":"c1","name":"bash"}]}}""", true)]
    public async Task IsEvent_Pi_OnlyCountsMessageUserOrAssistant(string line, bool expected) {
        await Assert.That(WatchCommand.IsEvent(line, "pi")).IsEqualTo(expected);
    }

    [Test]
    [Arguments("""{"type":"model_change","id":"d1","modelId":"gpt-5"}""")]
    [Arguments("""{"type":"message","id":"c1","message":{"role":"toolResult","toolCallId":"t1","content":[]}}""")]
    public async Task TitleHelpers_ReturnNull_ForNonConversationalPiLines(string line) {
        await Assert.That(WatchCommand.TryExtractUserText(line, "pi")).IsNull();
        await Assert.That(WatchCommand.TryExtractAssistantText(line, "pi")).IsNull();
    }
}
