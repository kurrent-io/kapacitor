using Capacitor.Cli.Commands.Harness;

namespace Capacitor.Cli.Tests.Unit.Harness.Kiro;

/// <summary>
/// The Kiro agentSpawn memory-injection contract. Kiro appends successful hook stdout DIRECTLY into
/// agent context, so these tests pin the two things that must never break: the bytes are the raw
/// fragment with no JSON envelope and no diagnostics, and every no-memory path writes nothing at all.
///
/// The once-per-session dedupe that makes this safe under Kiro's per-PROMPT agentSpawn lives in
/// SessionStartMemoryFoundationTests (the Kiro_* tests), next to the lease fixtures it needs.
/// </summary>
public class KiroSessionStartMemoryTests {
    static string Write(string? fragment) {
        var sw = new StringWriter();
        KiroHookCommand.WriteAgentSpawnOutput(sw, fragment);

        return sw.ToString();
    }

    // The regression that matters most. Kiro's hook wrote nothing to stdout before this feature, and
    // every no-memory path — opt-out, exclusion, provider failure, budget exhaustion, and (the common
    // case) a repeat agentSpawn whose lease is already spent — must stay byte-identical to that.
    // Anything emitted here becomes conversation context.
    [Test]
    public async Task no_fragment_writes_zero_bytes() {
        await Assert.That(Write(null)).IsEqualTo("");
    }

    // Raw text, not JSON: Kiro has no envelope contract. A stray envelope would inject literal
    // `{"additionalContext": ...}` braces into the model's context.
    [Test]
    public async Task a_fragment_is_written_as_raw_text_with_no_envelope() {
        var output = Write("## Team memory\n- prefer the integration suite");

        await Assert.That(output).IsEqualTo("## Team memory\n- prefer the integration suite\n");
        await Assert.That(output).DoesNotContain("additionalContext");
        await Assert.That(output).DoesNotContain("hookSpecificOutput");
        await Assert.That(output).DoesNotStartWith("{");
    }

    // Exactly one trailing terminator — not zero (which would run the fragment into whatever Kiro
    // appends next) and not two (which pads the context).
    [Test]
    public async Task output_ends_with_exactly_one_newline() {
        await Assert.That(Write("## Team memory")).IsEqualTo("## Team memory\n");
    }

    // JSON-escaping the fragment would corrupt it: markdown carries quotes, backslashes and newlines,
    // and Kiro reads the bytes literally. This is the inverse of the Codex/Copilot adapters' contract,
    // so it is pinned explicitly rather than assumed.
    [Test]
    [Arguments("quote \" backslash \\ tab \t")]
    [Arguments("non-BMP \U0001F600 emoji")]
    [Arguments("- item one\n- item two\n\n### Heading")]
    public async Task fragment_content_is_emitted_verbatim_without_escaping(string fragment) {
        await Assert.That(Write(fragment)).IsEqualTo(fragment + "\n");
    }

    // An empty (not null) fragment is a real, if degenerate, Ready payload; it must not gain a
    // spurious line of its own beyond the terminator.
    [Test]
    public async Task an_empty_fragment_writes_only_its_terminator() {
        await Assert.That(Write("")).IsEqualTo("\n");
    }
}
