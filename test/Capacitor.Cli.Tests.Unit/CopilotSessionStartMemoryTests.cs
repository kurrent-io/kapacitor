using Capacitor.Cli.Commands;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The Copilot SessionStart memory-injection contract. Copilot parses this hook's stdout as an
/// optional SINGLE JSON document, and today the hook writes nothing at all — so the tests pin both
/// halves of the decision: a fragment produces exactly one `additionalContext` object, and no
/// fragment produces byte-identical silence rather than a newly-emitted `{}`.
/// </summary>
public class CopilotSessionStartMemoryTests {
    static string Write(string? fragment) {
        var sw = new StringWriter();
        CopilotHookCommand.WriteSessionStartOutput(sw, fragment);

        return sw.ToString();
    }

    // The regression that matters: every no-memory path (opt-out, exclusion, provider failure,
    // budget exhaustion, ineligible source) funnels a null fragment through the writer. Copilot's
    // sessionStart hook emitted nothing before this feature, and must still emit nothing — not `{}`.
    [Test]
    public async Task no_fragment_writes_nothing_at_all() {
        await Assert.That(Write(null)).IsEqualTo("");
    }

    [Test]
    public async Task a_fragment_writes_one_top_level_additional_context_object() {
        var output = Write("## Team memory\n- prefer the integration suite");
        var parsed = System.Text.Json.JsonDocument.Parse(output);

        await Assert.That(parsed.RootElement.GetProperty("additionalContext").GetString())
            .IsEqualTo("## Team memory\n- prefer the integration suite");

        // Top-level only — Copilot's sessionStart contract has no hookSpecificOutput wrapper.
        await Assert.That(output).DoesNotContain("hookSpecificOutput");
    }

    // Copilot accepts one document; guard against a renderer that emits two.
    [Test]
    public async Task output_is_a_single_json_value_with_no_trailing_document() {
        var reader = new System.Text.Json.Utf8JsonReader(
            System.Text.Encoding.UTF8.GetBytes(Write("## Team memory")));
        reader.Read();
        reader.Skip();

        await Assert.That(reader.Read()).IsFalse();
    }

    [Test]
    [Arguments("quote \" backslash \\ newline \n tab \t")]
    [Arguments("non-BMP \U0001F600 and CR \r")]
    [Arguments("")]
    public async Task fragment_content_round_trips_through_escaping(string fragment) {
        var parsed = System.Text.Json.JsonDocument.Parse(Write(fragment));

        await Assert.That(parsed.RootElement.GetProperty("additionalContext").GetString())
            .IsEqualTo(fragment);
    }

    // Copilot DOES report a lifecycle source, unlike Codex — so the reason is mapped, not assumed.
    // `startup` is the value Copilot's own payload defaults to, and it must be eligible: treating it
    // as unknown would silently deny memory to the ordinary first-start case.
    // Compared by name: SessionLifecycleReason is internal, so it cannot appear in a public
    // signature (CS0051) — the mapping is what matters, not the parameter's static type.
    [Test]
    [Arguments("startup", "New")]
    [Arguments("new", "New")]
    [Arguments("resume", "Resume")]
    [Arguments("STARTUP", "New")]
    [Arguments(null, "New")]
    [Arguments("", "New")]
    public async Task a_reported_source_maps_to_its_lifecycle_reason(string? source, string expected) {
        await Assert.That(SessionStartMemoryHookSupport.ReasonFor(source).ToString()).IsEqualTo(expected);
    }

    // An unrecognised source must NOT be guessed as New: the lifecycle policy derives the injection
    // decision from this, so inventing a reason would invent a decision.
    [Test]
    public async Task an_unrecognised_source_maps_to_unknown_rather_than_being_guessed() {
        await Assert.That(SessionStartMemoryHookSupport.ReasonFor("teleported").ToString())
            .IsEqualTo("Unknown");
    }

    // The shared guard protects a process-exiting URL validator (EnsureAbsolute calls
    // Environment.Exit(2)); asserting the predicate, since tripping the exit would take the host down.
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("localhost:5108")]
    [Arguments("/relative")]
    public async Task an_unusable_base_url_is_refused_before_auth_discovery(string? baseUrl) {
        await Assert.That(SessionStartMemoryHookSupport.CanAttempt(baseUrl)).IsFalse();
    }

    [Test]
    [Arguments("http://localhost:5108")]
    [Arguments("https://kurrent.kcap.ai")]
    public async Task an_absolute_base_url_is_permitted(string baseUrl) {
        await Assert.That(SessionStartMemoryHookSupport.CanAttempt(baseUrl)).IsTrue();
    }
}
