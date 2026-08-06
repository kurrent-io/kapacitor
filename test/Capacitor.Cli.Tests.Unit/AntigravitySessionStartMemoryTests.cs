using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The Antigravity PreInvocation memory-injection contract. Antigravity reads hook stdout as a JSON
/// envelope (<c>injectSteps</c>), so these tests pin the two things that must never break: a fragment
/// renders as a durable <c>userMessage</c> step, and every no-memory path writes NOTHING AT ALL.
///
/// The zero-bytes rule is the regression that matters most. This hook emitted nothing before the
/// memory index existed, so rendering the adapter's <c>{}</c> on the no-fragment path would change
/// the wire behaviour of EVERY invocation for EVERY user — including the IDE-only majority, whose
/// product was never probed — to buy nothing. Copilot and Kiro set the precedent.
///
/// The once-per-conversation dedupe that makes this safe under PreInvocation's per-INVOCATION firing
/// lives in SessionStartMemoryFoundationTests (the Antigravity_* tests), next to the lease fixtures.
/// </summary>
public class AntigravitySessionStartMemoryTests {
    static string Write(string? fragment) {
        var sw = new StringWriter();
        AntigravityHookCommand.WritePreInvocationOutput(sw, fragment);

        return sw.ToString();
    }

    // Byte-identical to the pre-feature behaviour on every path a user gets no index: opt-out,
    // exclusion, provider failure, budget exhaustion, and (the common case) a repeat PreInvocation
    // whose lease is already spent. `{}` here would be a wire change on every invocation.
    [Test]
    public async Task no_fragment_writes_zero_bytes() {
        await Assert.That(Write(null)).IsEqualTo("");
    }

    // The vendor's own documented shape, verified against the agy binary's embedded hook contract:
    // injectSteps carries userMessage (durable) rather than ephemeralMessage (documented transient).
    [Test]
    public async Task a_fragment_renders_the_injectSteps_envelope() {
        await Assert.That(Write("F")).IsEqualTo("{\"injectSteps\":[{\"userMessage\":\"F\"}]}\n");
    }

    // Markdown carries quotes, backslashes and newlines; the envelope is JSON, so the fragment must
    // survive a round trip byte-for-byte. Asserted by PARSING rather than by matching escape
    // sequences: the serializer escapes `"` as " rather than \" (System.Text.Json's default
    // encoder), which is equally valid JSON, so pinning specific escapes would fail on a correct
    // implementation. What actually matters to the model is that it decodes back to the input.
    //
    // This is the inverse of Kiro's raw-stdout contract, so it is pinned rather than assumed.
    [Test]
    [Arguments("quote \" backslash \\ newline \n")]
    [Arguments("non-BMP \U0001F600 emoji")]
    [Arguments("- item one\n- item two\n\n### Heading")]
    public async Task a_fragment_round_trips_through_the_envelope(string fragment) {
        var output = Write(fragment);

        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var decoded = doc.RootElement
            .GetProperty("injectSteps")[0]
            .GetProperty("userMessage")
            .GetString();

        await Assert.That(decoded).IsEqualTo(fragment);
    }

    // Exactly one trailing terminator, and the envelope occupies a single line — a raw newline inside
    // the JSON would break any line-oriented reader on the vendor side.
    [Test]
    public async Task the_envelope_is_one_line_with_a_single_terminator() {
        var output = Write("- item one\n- item two");

        await Assert.That(output).EndsWith("\n");
        await Assert.That(output.TrimEnd('\n')).DoesNotContain("\n");
        await Assert.That(output.Split('\n').Length).IsEqualTo(2);   // content + the terminator
    }

    // An empty (not null) fragment is a real, if degenerate, Ready payload — it still renders the
    // envelope, because the null case is the ONLY zero-bytes case.
    [Test]
    public async Task an_empty_fragment_still_renders_the_envelope() {
        await Assert.That(Write("")).IsEqualTo("{\"injectSteps\":[{\"userMessage\":\"\"}]}\n");
    }
}
