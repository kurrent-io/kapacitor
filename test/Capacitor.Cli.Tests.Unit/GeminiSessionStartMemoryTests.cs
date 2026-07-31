using Capacitor.Cli.Commands;
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
        var output = Write("## Team memory\n- prefer the integration suite");

        await Assert.That(output).Contains("\"hookSpecificOutput\"");
        await Assert.That(output).Contains("\"hookEventName\":\"SessionStart\"");
        await Assert.That(output).Contains("\"additionalContext\"");
        await Assert.That(output).StartsWith("{");
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
        var roundTripped = doc.RootElement
            .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();

        await Assert.That(roundTripped).IsEqualTo(fragment);
    }

    [Test]
    [Arguments(null)]
    [Arguments("## Team memory")]
    public async Task every_payload_is_exactly_one_parseable_json_object(string? fragment) {
        var output = Write(fragment);

        using var doc = System.Text.Json.JsonDocument.Parse(output);
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Object);
    }

    // ── a failing writer must not change the command's exit code ──────────────

    sealed class ThrowingWriter(int failAfterChars) : StringWriter {
        int _written;
        public override void Write(string? value) {
            if (_written >= failAfterChars) throw new IOException("stdout closed");
            _written += value?.Length ?? 0;
            base.Write(value);
        }
    }

    /// <summary>A write that throws — before any byte, or mid-payload — must be swallowed. Rendering
    /// completes before the single write, so a partial payload is the only exposure, and Gemini degrades
    /// truncated JSON to plain text rather than synthesising a block.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(5)]
    public async Task a_throwing_writer_does_not_propagate(int failAfterChars) {
        var writer = new ThrowingWriter(failAfterChars);

        GeminiHookCommand.WriteSessionStartOutput(writer, "## Team memory");
        GeminiHookCommand.WriteSessionStartOutput(writer, null);

        await Assert.That(true).IsTrue();   // reaching here without an exception IS the assertion
    }

    // ── source → lifecycle mapping ────────────────────────────────────────────

    /// <summary><c>clear</c> maps to <c>New</c>, exactly as it does for Claude, so the session-id lease
    /// suppresses re-injection after a context reset. That is a KNOWN GAP tracked separately (there is no
    /// <c>Clear</c> reason in the foundation) — pinned here so the gap is deliberate and visible rather
    /// than an accident someone silently "fixes" for one harness.</summary>
    // Expected value is passed by NAME, not as the enum: SessionLifecycleReason is internal, so a public
    // test signature cannot mention it.
    [Test]
    [Arguments("startup", "New")]
    [Arguments("resume",  "Resume")]
    [Arguments("clear",   "New")]
    [Arguments("compact", "Compact")]
    [Arguments(null,      "New")]
    [Arguments("RESUME",  "Resume")]
    public async Task source_maps_to_the_shared_lifecycle_reason(string? source, string expected) {
        await Assert.That(GeminiHookCommand.LifecycleReasonFor(source).ToString()).IsEqualTo(expected);
    }
}
