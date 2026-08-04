using System.Text.Json;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Gemini's hook runner picks the text it parses as <c>stdout.trim() || stderr.trim()</c> — in a
/// <c>child.on("close")</c> handler that never consults the event name. So EVERY event kcap subscribes to
/// consumes kcap's STDERR as the hook result whenever kcap writes nothing to stdout, and kcap writes
/// failed-POST and auth diagnostics to stderr on all of them.
///
/// <para>These tests pin the shadowing invariant: a recognised hook firing makes exactly one write
/// ATTEMPT, on every returning path, so stdout wins the <c>||</c> whenever stdout is writable. They fail
/// if someone reintroduces an empty-stdout return alongside a stderr write — the regression this file
/// exists to prevent, which has now been shipped twice (Codex's early <c>return 1</c>, and every Gemini
/// event but SessionStart).</para>
///
/// <para>"Attempt", not "one object reaches stdout": a throwing writer still consumes the claim, leaving
/// stdout empty or truncated and the stderr fallback live.
/// <see cref="A_throwing_write_is_swallowed_and_still_consumes_the_single_claim"/> pins both shapes
/// deliberately. That is an accepted residue — retrying cannot distinguish them, and a second object
/// appended to a partial one is unparseable, which is itself what falls back to reading stderr — and a
/// stdout we cannot write to is not recoverable from inside this process. Do not let the stated
/// invariant drift back to the absolute form these very tests disprove.</para>
/// </summary>
public class GeminiHookOutputContractTests {
    const string SessionId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    // Every event kcap registers, plus one it does not. The unrecognised entry is deliberate: Gemini's
    // close handler is event-agnostic, so an event we do not route still has its stdout read, and a
    // version bump that renames an event must not silently reopen the stderr channel.
    public static IEnumerable<Func<string>> Events() {
        yield return () => "SessionStart";
        yield return () => "SessionEnd";
        yield return () => "Notification";
        yield return () => "PreToolUse";
    }

    [Test, MethodDataSource(nameof(Events)), NotInParallel]
    public async Task An_unusable_session_id_still_emits_a_non_blocking_result(string eventName) {
        // Gemini fired the hook and WILL read stdout whatever we make of the rest of the payload.
        var stdout = await RunAsync($$"""
            {"hook_event_name":"{{eventName}}","session_id":"not-a-guid","cwd":"/tmp"}
            """);

        await AssertNonBlockingJsonObject(stdout);
    }

    [Test, MethodDataSource(nameof(Events)), NotInParallel]
    public async Task A_disabled_session_still_emits_a_non_blocking_result(string eventName) {
        // `kcap disable` suppresses every POST and watcher restart — but suppression is not silence.
        // Program.cs drains the spool before dispatch, so these paths are not stderr-free.
        DisabledSessions.Mark(SessionId.Replace("-", ""));

        try {
            var stdout = await RunAsync($$"""
                {"hook_event_name":"{{eventName}}","session_id":"{{SessionId}}","cwd":"/tmp"}
                """);

            await AssertNonBlockingJsonObject(stdout);
        } finally {
            DisabledSessions.RemoveMarker(SessionId.Replace("-", ""));
        }
    }

    [Test, NotInParallel]
    public async Task A_notification_missing_its_required_fields_still_emits_a_non_blocking_result() {
        // The server's NotificationHook needs message + notification_type; without them the forward is
        // abandoned. That abandonment used to return zero bytes.
        var stdout = await RunAsync($$"""
            {"hook_event_name":"Notification","session_id":"{{SessionId}}","cwd":"/tmp"}
            """);

        await AssertNonBlockingJsonObject(stdout);
    }

    [Test, NotInParallel]
    public async Task Input_that_is_not_provably_a_hook_firing_stays_silent() {
        // The deliberate exception to the invariant, and the reason it is stated as "a RECOGNISED
        // firing": with no parseable `hook_event_name` we cannot know Gemini invoked us as a hook at
        // all, and emitting a decision object into some other consumer's stdout is its own hazard.
        await Assert.That(await RunAsync("not json at all")).IsEmpty();
        await Assert.That(await RunAsync("""{"session_id":"x"}""")).IsEmpty();
        await Assert.That(await RunAsync("""{"hook_event_name":""}""")).IsEmpty();
    }

    /// <summary>
    /// The invariant that keeps a *parse failure* non-blocking, pinned because nothing else states it.
    ///
    /// <para>Gemini's plain-text fallback is not unconditionally benign: it maps exit 0 and 1 to
    /// <c>decision: "allow"</c> but ANY other code to <c>decision: "deny"</c>. kcap stays out of that band
    /// only because <c>hook</c> is a fail-open command — which is what turns
    /// <c>EnsureAbsolute</c>'s <c>Environment.Exit(2)</c> into a throw and makes the top-level catch
    /// return 0. Drop <c>"hook"</c> from that set and a malformed <c>server_url</c> becomes a stderr
    /// string that BLOCKS the Gemini session.</para>
    /// </summary>
    [Test]
    public async Task The_hook_command_stays_out_of_geminis_blocking_exit_code_band() {
        await Assert.That(CrashReporter.IsFailOpenCommand("hook")).IsTrue();
        await Assert.That(CrashReporter.ExitCode("hook")).IsLessThan(2);
    }

    // ── the sink's own contract ───────────────────────────────────────────────

    /// <summary>
    /// The backstop must not append a SECOND object behind a path that already wrote a real one — two
    /// concatenated objects are not parseable JSON, so Gemini would fall back to reading the pair as
    /// plain text, which is the very channel this design closes.
    /// </summary>
    [Test]
    public async Task The_backstop_does_not_append_behind_a_payload_already_written() {
        var writer = new StringWriter();
        var sink   = new GeminiHookCommand.HookResultWriter(writer);

        sink.Write("""{"hookSpecificOutput":{"additionalContext":"memory"}}""");
        sink.EnsureWritten();

        await Assert.That(writer.ToString()).IsEqualTo("""{"hookSpecificOutput":{"additionalContext":"memory"}}""");
        using var doc = JsonDocument.Parse(writer.ToString());   // throwing IS the failure
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
    }

    /// <summary>Writes <paramref name="charsBeforeThrowing"/> characters and THEN throws, so 0 exercises
    /// "fails before any byte" and a positive value a genuine partial write. An earlier version of this
    /// writer only ever threw before writing, leaving the advertised partial-write case untested.</summary>
    sealed class ThrowingWriter(int charsBeforeThrowing) : StringWriter {
        public override void Write(string? value) {
            if (value is { Length: > 0 } && charsBeforeThrowing > 0)
                base.Write(value[..Math.Min(charsBeforeThrowing, value.Length)]);

            throw new IOException("stdout closed");
        }
    }

    /// <summary>
    /// A stdout write that throws — before any byte, or mid-payload — must be swallowed, so it cannot
    /// alter the command's exit code and push it into Gemini's <c>deny</c> band. It must ALSO still
    /// consume the single claim: retrying would append a second object onto a partial one, and two
    /// concatenated objects are exactly the unparseable input that falls back to plain text.
    ///
    /// <para>A truncated payload on its own is safe — Gemini degrades truncated JSON to plain text and,
    /// at the exit codes this command returns, that stays an allow.</para>
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(5)]
    public async Task A_throwing_write_is_swallowed_and_still_consumes_the_single_claim(int charsBeforeThrowing) {
        var writer = new ThrowingWriter(charsBeforeThrowing);
        var sink   = new GeminiHookCommand.HookResultWriter(writer);

        sink.Write("""{"hookSpecificOutput":{"additionalContext":"memory"}}""");
        sink.EnsureWritten();   // must not propagate, and must not write a second object

        // Reaching here without an exception is half the contract. The length proves the other half AND
        // that the writer really threw where the case name claims — neither case can pass vacuously.
        await Assert.That(writer.ToString().Length).IsEqualTo(charsBeforeThrowing);
    }

    /// <summary>Asserts what Gemini would actually do with these bytes, not merely that some were
    /// written: the runner must select stdout, parse it as an object, and find no blocking decision.</summary>
    static async Task AssertNonBlockingJsonObject(string stdout) {
        await Assert.That(stdout.Trim()).IsNotEmpty();

        using var doc = JsonDocument.Parse(stdout.Trim());

        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);

        // `isBlockingDecision()` is `decision === "block" || decision === "deny"`.
        if (doc.RootElement.TryGetProperty("decision", out var decision)) {
            await Assert.That(decision.GetString()).IsNotEqualTo("block");
            await Assert.That(decision.GetString()).IsNotEqualTo("deny");
        }
    }

    static async Task<string> RunAsync(string payload) {
        var originalOut = Console.Out;
        var writer      = new StringWriter();
        Console.SetOut(writer);

        try {
            // A URL no POST can reach: these paths must all return before any network call, and a test
            // that quietly started talking to a live server would be measuring something else.
            await GeminiHookCommand.Handle("http://127.0.0.1:1", new StringReader(payload));

            return writer.ToString();
        } finally {
            Console.SetOut(originalOut);
        }
    }
}
