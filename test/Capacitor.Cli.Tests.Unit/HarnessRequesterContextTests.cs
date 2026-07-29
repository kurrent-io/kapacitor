using Capacitor.Cli;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Pins the requester-context resolution an MCP stdio server uses to answer "which session am I
/// serving, and where is it working?".
///
/// <para>The live failure these cover: a driver session launched from another session's shell
/// inherits the launcher's <c>KCAP_SESSION_ID</c> and (when the MCP registration pins no cwd) the
/// launcher's working directory. Nothing rewrites either for the child's MCP servers, so every flow
/// the driver started was attributed to the PARENT session, and the reviewer was handed the parent's
/// checkout — silently producing the wrong diff.</para>
///
/// <para>All of these inject the environment lookup rather than mutating the process environment, so
/// they stay parallel-safe (and can't be perturbed by whatever harness happens to be running the
/// suite — which is itself a session with these variables set).</para>
/// </summary>
public class HarnessRequesterContextTests {
    const string RunningDriverSession = "11111111-1111-1111-1111-111111111111";
    const string InheritedParentSession = "22222222222222222222222222222222";

    static Func<string, string?> Env(Dictionary<string, string?> values) =>
        key => values.TryGetValue(key, out var value) ? value : null;

    [Test]
    public async Task Resolves_the_running_harness_session_not_the_inherited_env_var() {
        // The shape of the live defect: an ambient KCAP_SESSION_ID naming the LAUNCHING session,
        // alongside the running harness's own per-process session id. The running one must win.
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["KCAP_SESSION_ID"]        = InheritedParentSession,
                ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession
            }),
            directoryExists: _ => true);

        await Assert.That(resolved.SessionId).IsEqualTo("11111111111111111111111111111111");
        await Assert.That(resolved.SessionId).IsNotEqualTo(InheritedParentSession);
    }

    [Test]
    public async Task Resolves_the_running_harness_project_dir_so_the_caller_ignores_an_inherited_cwd() {
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
                ["CLAUDE_PROJECT_DIR"]     = "/repos/driver-worktree"
            }),
            directoryExists: path => path == "/repos/driver-worktree");

        await Assert.That(resolved.ProjectDir).IsEqualTo("/repos/driver-worktree");
    }

    [Test]
    public async Task Project_dir_that_no_longer_exists_degrades_to_the_process_cwd() {
        // A null ProjectDir is the caller's signal to keep using Directory.GetCurrentDirectory() —
        // sending the server a path nothing can be checked out at would be worse than the cwd.
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
                ["CLAUDE_PROJECT_DIR"]     = "/repos/deleted-worktree"
            }),
            directoryExists: _ => false);

        await Assert.That(resolved.ProjectDir).IsNull();
        // The session id is still resolved — an unusable directory says nothing about identity.
        await Assert.That(resolved.SessionId).IsEqualTo("11111111111111111111111111111111");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Blank_project_dir_is_treated_as_unreported(string projectDir) {
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
                ["CLAUDE_PROJECT_DIR"]     = projectDir
            }),
            // Deliberately permissive: a blank path must be rejected before it is probed, not
            // accepted because some filesystem stub said yes.
            directoryExists: _ => true);

        await Assert.That(resolved.ProjectDir).IsNull();
    }

    [Test]
    public async Task Without_a_harness_signal_the_ambient_kcap_session_is_still_used() {
        // Every non-Claude-Code harness keeps the pre-existing behaviour: KCAP_SESSION_ID is the
        // best (and only) evidence available, and no project dir is claimed.
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() { ["KCAP_SESSION_ID"] = InheritedParentSession }),
            directoryExists: _ => true);

        await Assert.That(resolved.SessionId).IsEqualTo(InheritedParentSession);
        await Assert.That(resolved.ProjectDir).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("  ")]
    public async Task Blank_harness_session_falls_through_to_the_ambient_signals(string harnessSessionId) {
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["CLAUDE_CODE_SESSION_ID"] = harnessSessionId,
                ["CODEX_THREAD_ID"]        = "abc-def"
            }),
            directoryExists: _ => true);

        await Assert.That(resolved.SessionId).IsEqualTo("abcdef");
        await Assert.That(resolved.ProjectDir).IsNull();
    }

    [Test]
    public async Task Ambient_precedence_between_the_non_harness_signals_is_unchanged() {
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["KCAP_SESSION_ID"] = InheritedParentSession,
                ["CODEX_THREAD_ID"] = "abc-def"
            }),
            directoryExists: _ => true);

        await Assert.That(resolved.SessionId).IsEqualTo(InheritedParentSession);
    }

    [Test]
    public async Task A_nested_harness_signal_makes_the_claude_evidence_unprovable() {
        // codex-inside-claude (or the reverse): the inner harness inherits the outer's variables and
        // leaves them in place, so relocating the requester to CLAUDE_PROJECT_DIR could point at the
        // OUTER session's checkout. Resolution must fall back to the ambient behaviour instead —
        // never worse than what shipped before, which is the whole point of the guard.
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
                ["CLAUDE_PROJECT_DIR"]     = "/repos/outer-harness-checkout",
                ["CODEX_THREAD_ID"]        = "77777777-7777-7777-7777-777777777777",
                ["KCAP_SESSION_ID"]        = InheritedParentSession
            }),
            directoryExists: _ => true);

        await Assert.That(resolved.ProjectDir).IsNull();
        await Assert.That(resolved.SessionId).IsEqualTo(InheritedParentSession);
    }

    [Test]
    public async Task No_signal_at_all_resolves_nothing() {
        var resolved = HarnessRequesterContext.Resolve(Env(new()), directoryExists: _ => true);

        await Assert.That(resolved.SessionId).IsNull();
        await Assert.That(resolved.ProjectDir).IsNull();
    }
}
