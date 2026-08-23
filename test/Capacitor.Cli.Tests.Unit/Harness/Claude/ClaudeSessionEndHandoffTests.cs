using System.Diagnostics;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

/// <summary>
/// Claude Code gives a plugin-sourced SessionEnd hook 1.5 s, so the hook must hand the real
/// work to a detached continuation and return. These cover the hand-off decision and the shape
/// of the spawned process; the end-to-end timing lives in the integration suite.
/// </summary>
// Bare: WatcherManager.ProcessStarterForTesting is a mutable production static that every
// spawn path reads, and Dispose resets it.
[NotInParallel]
public class ClaudeSessionEndHandoffTests : IDisposable {
    static readonly string[] HookArgs = ["hook", "--claude", "--no-update-check"];

    const string SessionEndBody =
        """{"hook_event_name":"SessionEnd","session_id":"9dc27753-7645-4e46-91ec-c2d69973c152","reason":"exit"}""";

    public void Dispose() => WatcherManager.ProcessStarterForTesting = null;

    [Test]
    [Arguments("SessionEnd")]
    [Arguments("session-end")]
    [Arguments("session_end")]
    public async Task SessionEnd_is_handed_off(string eventName) {
        var body = $$"""{"hook_event_name":"{{eventName}}","session_id":"abc"}""";

        await Assert.That(ClaudeSessionEndHandoff.ShouldHandOff(HookArgs, body)).IsTrue();
    }

    [Test]
    [Arguments("""{"hook_event_name":"SessionStart","session_id":"abc"}""")]
    [Arguments("""{"hook_event_name":"Stop","session_id":"abc"}""")]
    [Arguments("""{"hook_event_name":"SubagentStop","session_id":"abc"}""")]
    [Arguments("""{"session_id":"abc"}""")]
    [Arguments("not json")]
    [Arguments("")]
    public async Task Other_events_and_malformed_payloads_stay_inline(string body) {
        await Assert.That(ClaudeSessionEndHandoff.ShouldHandOff(HookArgs, body)).IsFalse();
    }

    [Test]
    public async Task The_detached_continuation_never_hands_off_again() {
        string[] detached = [..HookArgs, ClaudeSessionEndHandoff.DetachedFlag];

        await Assert.That(ClaudeSessionEndHandoff.IsDetached(detached)).IsTrue();
        await Assert.That(ClaudeSessionEndHandoff.ShouldHandOff(detached, SessionEndBody)).IsFalse();
    }

    [Test]
    public async Task Spawn_reinvokes_this_binary_with_the_hook_args_plus_the_detached_flag() {
        ProcessStartInfo? seen = null;
        WatcherManager.ProcessStarterForTesting = psi => { seen = psi; return null; };

        var spawned = ClaudeSessionEndHandoff.TrySpawn(HookArgs, SessionEndBody);

        // A null start is a failed spawn: the caller falls back to the inline path.
        await Assert.That(spawned).IsFalse();
        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.FileName).IsEqualTo(Environment.ProcessPath);
        await Assert.That(seen.ArgumentList).IsEquivalentTo(["hook", "--claude", "--no-update-check", ClaudeSessionEndHandoff.DetachedFlag]);
        // Detached from Claude's pipes: every std stream is a private pipe the hook closes at once.
        await Assert.That(seen.RedirectStandardInput).IsTrue();
        await Assert.That(seen.RedirectStandardOutput).IsTrue();
        await Assert.That(seen.RedirectStandardError).IsTrue();
        await Assert.That(seen.UseShellExecute).IsFalse();
        // The continuation resolves the server URL itself, from the same cwd — the hook skipped
        // that work, so it has nothing to hand down.
        await Assert.That(seen.Environment.TryGetValue("KCAP_URL", out var url) ? url : null)
            .IsEqualTo(Environment.GetEnvironmentVariable("KCAP_URL"));
        await Assert.That(seen.WorkingDirectory).IsEqualTo("");
    }

    [Test]
    public async Task Spawn_pipes_the_hook_payload_to_the_continuation_stdin() {
        Skip.When(OperatingSystem.IsWindows(), "uses /bin/sh to stand in for the continuation");

        using var tmp  = new TempDir();
        var       sink = tmp.PathTo("stdin.txt");

        // Stand in for the kcap continuation with a shell that copies stdin to a file, keeping the
        // redirects the hand-off asked for so the write path under test is the real one.
        WatcherManager.ProcessStarterForTesting = psi => {
            var stub = new ProcessStartInfo("/bin/sh") {
                RedirectStandardInput  = psi.RedirectStandardInput,
                RedirectStandardOutput = psi.RedirectStandardOutput,
                RedirectStandardError  = psi.RedirectStandardError,
                UseShellExecute        = false,
            };
            stub.ArgumentList.Add("-c");
            stub.ArgumentList.Add($"cat > '{sink}'");
            return Process.Start(stub);
        };

        var spawned = ClaudeSessionEndHandoff.TrySpawn(HookArgs, SessionEndBody);

        await Assert.That(spawned).IsTrue();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && (!File.Exists(sink) || new FileInfo(sink).Length < SessionEndBody.Length))
            await Task.Delay(50);

        await Assert.That(File.ReadAllText(sink)).IsEqualTo(SessionEndBody);
    }

    [Test]
    public async Task Spawn_failure_is_reported_not_thrown() {
        WatcherManager.ProcessStarterForTesting = _ => throw new InvalidOperationException("no exec");

        await Assert.That(ClaudeSessionEndHandoff.TrySpawn(HookArgs, SessionEndBody)).IsFalse();
    }

    [Test]
    public async Task A_child_that_never_receives_the_payload_is_killed_before_the_inline_fallback() {
        Skip.When(OperatingSystem.IsWindows(), "uses /bin/sh to stand in for the continuation");

        // A started child whose stdin was not redirected: the payload write throws after the
        // process exists, the shape of any post-start failure.
        var pid = 0;
        var identity = "";
        WatcherManager.ProcessStarterForTesting = _ => {
            var stub = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            stub.ArgumentList.Add("-c");
            stub.ArgumentList.Add("sleep 30");
            var child = Process.Start(stub)!;
            // TrySpawn disposes its handle, so the pid plus start identity is captured here.
            pid      = child.Id;
            identity = PidIdentity.Capture(pid);
            return child;
        };

        var spawned = ClaudeSessionEndHandoff.TrySpawn(HookArgs, SessionEndBody);

        await Assert.That(spawned).IsFalse();
        await Assert.That(pid).IsNotEqualTo(0);
        await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(10));
    }

    [Test]
    [Arguments("""{"session_id":"9dc27753-7645-4e46-91ec-c2d69973c152"}""", "9dc2775376454e4691ecc2d69973c152")]
    [Arguments("""{"session_id":"../../etc/passwd"}""", "claude-session-end")]
    [Arguments("""{"session_id":""}""", "claude-session-end")]
    [Arguments("""{}""", "claude-session-end")]
    [Arguments("not json", "claude-session-end")]
    public async Task Log_name_is_the_watcher_key_or_a_fixed_name(string body, string expected) {
        await Assert.That(ClaudeSessionEndHandoff.LogName(body)).IsEqualTo(expected);
    }
}
