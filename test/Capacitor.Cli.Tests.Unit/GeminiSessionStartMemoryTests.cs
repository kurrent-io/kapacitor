using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The Gemini SessionStart memory-injection contract.
///
/// <para>Gemini differs from every other adapter in one way that drives most of these tests: its hook
/// stdout is a JSON <b>decision channel</b>, and its runner selects the text to parse as
/// <c>stdout.trim() || stderr.trim()</c>. So an empty stdout makes Gemini parse the hook's STDERR —
/// where kcap writes failed-POST and auth-lapse diagnostics — as the hook's output. That is why the
/// no-fragment path emits an explicit allow object instead of the zero bytes every other adapter
/// writes, and why that divergence is pinned here rather than left to be "harmonised" away.</para>
///
/// <para>The worst case it prevents: with memory injection opted OUT, a failed lifecycle POST could
/// otherwise put kcap text into the model's context.</para>
/// </summary>
public class GeminiSessionStartMemoryTests {
    static string Write(string? fragment) {
        var sw = new StringWriter();
        GeminiHookCommand.WriteSessionStartOutput(sw, fragment);

        return sw.ToString();
    }

    // ── the divergence from every other adapter ───────────────────────────────

    /// <summary>The load-bearing one. Zero bytes here would re-expose stderr to Gemini's parser.</summary>
    [Test]
    public async Task no_fragment_writes_an_explicit_allow_object_not_zero_bytes() {
        var output = Write(null);

        await Assert.That(output).IsNotEmpty();
        await Assert.That(output).IsEqualTo("""{"continue":true}""");
    }

    /// <summary>The allow object must carry NO <c>hookSpecificOutput</c> key: Gemini's
    /// <c>getAdditionalContext()</c> short-circuits on its own <c>"additionalContext" in
    /// hookSpecificOutput</c> guard, so an absent key contributes nothing. A present-but-empty one would
    /// be a shape we have not verified.</summary>
    [Test]
    public async Task the_allow_object_contributes_no_context_and_no_user_visible_message() {
        var output = Write(null);

        await Assert.That(output).DoesNotContain("hookSpecificOutput");
        await Assert.That(output).DoesNotContain("additionalContext");
        await Assert.That(output).DoesNotContain("systemMessage");
    }

    // ── neither payload may look like a blocking decision ─────────────────────

    /// <summary>Gemini blocks only on an explicit <c>decision: "block"|"deny"</c>
    /// (<c>isBlockingDecision()</c>), and stops only via <c>continue</c>/<c>stopReason</c>. Neither
    /// payload may ever carry those, or a memory injection would abort the user's session at startup.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("## Team memory\n- prefer the integration suite")]
    public async Task no_payload_ever_carries_a_blocking_or_stopping_field(string? fragment) {
        var output = Write(fragment);

        await Assert.That(output).DoesNotContain("\"decision\"");
        await Assert.That(output).DoesNotContain("\"stopReason\"");
        await Assert.That(output).DoesNotContain("\"continue\":false");
    }

    // ── the fragment envelope ─────────────────────────────────────────────────

    [Test]
    public async Task a_fragment_is_written_as_the_claude_shaped_envelope() {
        const string fragment = "## Team memory\n- prefer the integration suite";
        using var doc = System.Text.Json.JsonDocument.Parse(Write(fragment));

        // Structural, via the project's JsonElement helpers — substring matching would pass on a
        // malformed document that merely contained the right characters.
        var hookSpecific = doc.RootElement.Obj("hookSpecificOutput");
        await Assert.That(hookSpecific).IsNotNull();
        await Assert.That(hookSpecific!.Value.Str("hookEventName")).IsEqualTo("SessionStart");
        await Assert.That(hookSpecific!.Value.Str("additionalContext")).IsEqualTo(fragment);
    }

    /// <summary>Inverse of the Kiro contract: Gemini reads JSON, so the fragment MUST be escaped.
    /// Emitting raw markdown would produce invalid JSON — which Gemini degrades to plain text rather
    /// than blocking, but which silently loses the index.</summary>
    [Test]
    [Arguments("quote \" backslash \\ tab \t")]
    [Arguments("- item one\n- item two\n\n### Heading")]
    public async Task fragment_content_is_json_escaped(string fragment) {
        var output = Write(fragment);

        await Assert.That(output).DoesNotContain("\n## ");
        // Round-trips back to the original: proof the escaping is correct, not merely present.
        using var doc = System.Text.Json.JsonDocument.Parse(output);

        await Assert.That(doc.RootElement.Obj("hookSpecificOutput")?.Str("additionalContext")).IsEqualTo(fragment);
    }

    [Test]
    [Arguments(null)]
    [Arguments("## Team memory")]
    public async Task every_payload_is_exactly_one_parseable_json_object(string? fragment) {
        var output = Write(fragment);

        // Parse() throwing IS the failure; Obj/Str return null on a non-object root, so a non-object
        // payload fails the follow-up too.
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var isObject = doc.RootElement.Obj("hookSpecificOutput") is not null
                    || doc.RootElement.Str("continue") is not null
                    || output == GeminiHookCommand.AllowPayload;

        await Assert.That(isObject).IsTrue();
    }

    // ── a failing writer must not change the command's exit code ──────────────

    /// <summary>Writes <paramref name="charsBeforeThrowing"/> characters of the payload and THEN throws,
    /// so 0 exercises "fails before any byte" and a positive value exercises a genuine partial write.
    /// An earlier version only ever threw before writing, leaving the advertised partial-write case
    /// untested — caught in review.</summary>
    sealed class ThrowingWriter(int charsBeforeThrowing) : StringWriter {
        public override void Write(string? value) {
            if (value is { Length: > 0 } && charsBeforeThrowing > 0)
                base.Write(value[..Math.Min(charsBeforeThrowing, value.Length)]);

            throw new IOException("stdout closed");
        }
    }

    /// <summary>A write that throws — before any byte, or mid-payload — must be swallowed. Rendering
    /// completes before the single write, so a partial payload is the only exposure, and Gemini degrades
    /// truncated JSON to plain text rather than synthesising a block.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(5)]
    public async Task a_throwing_writer_does_not_propagate(int charsBeforeThrowing) {
        var writer = new ThrowingWriter(charsBeforeThrowing);

        GeminiHookCommand.WriteSessionStartOutput(writer, "## Team memory");
        GeminiHookCommand.WriteSessionStartOutput(writer, null);

        // Reaching here without an exception is the contract. Also prove the writer really did what the
        // case name claims, so neither case can pass by never throwing at all.
        await Assert.That(writer.ToString().Length).IsEqualTo(charsBeforeThrowing == 0 ? 0 : charsBeforeThrowing * 2);
    }

    // ── the invariant at the Handle level, not just the writer ────────────────

    /// <summary>
    /// A `SessionStart` whose `session_id` is missing or not a GUID still emits.
    ///
    /// <para>Found by code review. The event is recognisable from `hook_event_name` alone, and Gemini
    /// reads our stdout regardless of what we make of the rest of the payload — so returning silently
    /// here re-exposed the `stdout || stderr` fallback. The writer-level tests could not catch it: they
    /// call <c>WriteSessionStartOutput</c> directly and never exercise <c>Handle</c>'s early returns.</para>
    /// </summary>
    [Test, NotInParallel]
    [Arguments("""{"hook_event_name":"SessionStart"}""")]
    [Arguments("""{"hook_event_name":"SessionStart","session_id":""}""")]
    [Arguments("""{"hook_event_name":"SessionStart","session_id":"not-a-guid"}""")]
    public async Task session_start_with_an_unusable_session_id_still_emits_exactly_one_json_object(string payload) {
        var captured = await CaptureHandleStdout(payload);

        await Assert.That(captured).IsEqualTo(GeminiHookCommand.AllowPayload);
        using var doc = System.Text.Json.JsonDocument.Parse(captured);   // throwing IS the failure
        await Assert.That(doc.RootElement.Obj("hookSpecificOutput")).IsNull();
    }

    /// <summary>The complement: input we genuinely cannot recognise as a SessionStart stays silent —
    /// Gemini fired something else (or nothing parseable) and we have no result to attribute.</summary>
    [Test, NotInParallel]
    [Arguments("not json at all")]
    [Arguments("""{"session_id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301"}""")]
    [Arguments("""{"hook_event_name":"","session_id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301"}""")]
    public async Task unrecognisable_input_stays_silent(string payload) {
        await Assert.That(await CaptureHandleStdout(payload)).IsEqualTo("");
    }

    static async Task<string> CaptureHandleStdout(string payload) {
        var original = Console.Out;
        var sw = new StringWriter();

        try {
            Console.SetOut(sw);
            // baseUrl is unreachable on purpose: these paths must return before any network work.
            await GeminiHookCommand.Handle("http://127.0.0.1:1", new StringReader(payload));
        } finally {
            Console.SetOut(original);
        }

        return sw.ToString();
    }

    // ── source → lifecycle mapping ────────────────────────────────────────────

    /// <summary>
    /// The adapter uses the SHARED <c>SessionStartMemoryHookSupport.ReasonFor</c>, not a local mapper.
    ///
    /// <para>An earlier revision hand-rolled one defaulting unrecognised sources to <c>New</c>. That is a
    /// real bug, not a style point: the lifecycle policy decides eligibility from this reason, so
    /// defaulting to <c>New</c> would inject on an unverified source AND spend the once-per-session lease
    /// on it. The shared mapper returns <c>Unknown</c>, which the policy suppresses.</para>
    ///
    /// <para><c>clear</c> therefore lands on <c>Unknown</c> and is suppressed by the POLICY rather than by
    /// the lease. Same observable outcome — no re-injection after a context reset — but suppressed
    /// explicitly. Re-injection on clear is a known foundation-wide gap tracked separately; pinned here so
    /// it stays deliberate and visible.</para>
    /// </summary>
    // Expected value is passed by NAME: SessionLifecycleReason is internal, so a public test signature
    // cannot mention it.
    [Test]
    [Arguments("startup", "New")]
    [Arguments("resume",  "Resume")]
    [Arguments("compact", "Compact")]
    [Arguments(null,      "New")]
    [Arguments("RESUME",  "Resume")]
    [Arguments("clear",   "Unknown")]
    [Arguments("wat",     "Unknown")]
    public async Task source_maps_through_the_shared_lifecycle_mapper(string? source, string expected) {
        await Assert.That(SessionStartMemoryHookSupport.ReasonFor(source).ToString()).IsEqualTo(expected);
    }
}
