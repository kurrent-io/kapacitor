using System.Runtime.CompilerServices;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The Codex SessionStart memory-injection envelope contract. Codex blocks on this hook's stdout
/// and its parser accepts exactly ONE JSON value, so these tests pin three things the adapter must
/// never break: the byte-for-byte minimal handshake when no fragment exists, a single well-formed
/// combined object when one does, and that a fragment never leaks into Stop's output.
///
/// Drives <see cref="CodexHookCommand.WriteSessionStartOutput"/> (the SessionStart writer) directly
/// rather than the full hook, so no server, git repo, or config root is required.
/// </summary>
public class CodexSessionStartMemoryTests {
    static string Write(string? fragment) {
        var sw = new StringWriter();
        CodexHookCommand.WriteSessionStartOutput(sw, fragment);

        return sw.ToString();
    }

    // The regression that matters most: every no-memory path (opt-out, exclusion, provider
    // failure, budget exhaustion) funnels a null fragment through the writer, and the bytes
    // must be indistinguishable from the pre-memory handshake. If the shared adapter ever
    // renders `{"continue":true,"hookSpecificOutput":null}` or reorders keys, Codex's parser
    // contract changes silently — this test is the tripwire.
    [Test]
    public async Task no_fragment_emits_the_byte_identical_minimal_handshake() {
        var sw = new StringWriter();
        CodexHookCommand.WriteSessionScopedOutput(sw);

        await Assert.That(Write(null)).IsEqualTo(sw.ToString());
        await Assert.That(Write(null)).IsEqualTo("""{"continue":true}""");
    }

    // The fragment-bearing shape is rendered by the shared adapter, which appends a trailing
    // newline to every envelope (as Claude and Cursor already ship). Pinned so the asymmetry with
    // the null case above is a recorded decision rather than an accident.
    [Test]
    public async Task a_fragment_bearing_payload_carries_the_shared_adapters_trailing_newline() {
        await Assert.That(Write("## Team memory")).EndsWith("\n");
        await Assert.That(Write(null)).DoesNotContain("\n");
    }

    [Test]
    public async Task a_fragment_emits_one_combined_object_carrying_continue_and_additional_context() {
        var output = Write("## Team memory\n- always run the integration suite");

        // Exactly one JSON value, and it parses.
        var parsed = System.Text.Json.JsonDocument.Parse(output);

        await Assert.That(parsed.RootElement.GetProperty("continue").GetBoolean()).IsTrue();

        var hookOutput = parsed.RootElement.GetProperty("hookSpecificOutput");

        await Assert.That(hookOutput.GetProperty("hookEventName").GetString()).IsEqualTo("SessionStart");
        await Assert.That(hookOutput.GetProperty("additionalContext").GetString())
            .IsEqualTo("## Team memory\n- always run the integration suite");
    }

    // Codex's parser rejects a second JSON value on stdout. Guard against a future renderer that
    // emits the handshake and the memory object as two documents.
    [Test]
    public async Task output_is_a_single_json_value_with_no_trailing_document() {
        var output = Write("## Team memory\n- one");

        var reader = new System.Text.Json.Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(output));
        reader.Read();
        reader.Skip();

        await Assert.That(reader.Read()).IsFalse();
    }

    // Control/quote/newline/non-BMP content must survive JSON escaping intact — the fragment is
    // server-rendered markdown and carries all of these.
    [Test]
    [Arguments("quote \" backslash \\ newline \n tab \t")]
    [Arguments("non-BMP \U0001F600 and CR \r")]
    [Arguments("")]
    public async Task fragment_content_round_trips_through_escaping(string fragment) {
        var parsed = System.Text.Json.JsonDocument.Parse(Write(fragment));

        await Assert.That(parsed.RootElement.GetProperty("hookSpecificOutput")
                               .GetProperty("additionalContext").GetString())
            .IsEqualTo(fragment);
    }

    // The authenticated-client helper validates the URL by printing a hint and calling
    // Environment.Exit(2). Reaching that from SessionStart would kill the hook BEFORE the stdout
    // handshake, so Codex would receive no output and reject the session — far worse than skipping
    // an optional memory fragment. The guard must therefore reject anything unacceptable BEFORE
    // auth discovery. (Asserting the predicate, not the exit: a test that actually tripped
    // Environment.Exit would take the test host down with it.)
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("localhost:5108")]
    [Arguments("/relative/path")]
    public async Task an_unusable_base_url_skips_memory_injection_instead_of_exiting(string? baseUrl) {
        await Assert.That(CodexHookCommand.CanAttemptMemoryInjection(baseUrl)).IsFalse();
    }

    [Test]
    [Arguments("http://localhost:5108")]
    [Arguments("https://kurrent.kcap.ai")]
    public async Task an_absolute_base_url_permits_memory_injection(string baseUrl) {
        await Assert.That(CodexHookCommand.CanAttemptMemoryInjection(baseUrl)).IsTrue();
    }

    /// <summary>Walks up from this file's compile-time path to the repo root.</summary>
    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);

        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"repo root not found from {here}");
    }

    // The memory index endpoint is bearer-authenticated, and the shared provider hands a rejected
    // bearer back to the client factory so it can mint a refreshed client. A bare `new HttpClient()`
    // would therefore 401 on BOTH the initial call and the refresh, the provider would record a
    // retryable failure, and Codex would silently receive no memory on every authenticated
    // deployment — a failure invisible to the writer-level tests above and to any test that cannot
    // resolve a real token.
    //
    // A credential-attaching assertion needs KCAP_CONFIG_DIR bound before PathHelpers' static
    // initializer runs, which a parallel shared test assembly cannot guarantee. So this pins the
    // regression at the source level instead: the production path must route through the shared
    // authenticated-client helper, and must construct no bare client for the memory factory.
    [Test]
    public async Task the_production_memory_client_factory_routes_through_the_authenticated_helper() {
        var source = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "src", "Capacitor.Cli", "Commands", "CodexHookCommand.cs"));

        // The memory path must be wired to the named production factory, not an inline client.
        await Assert.That(source).Contains("memoryClientFactory ?? DefaultMemoryClientFactory(baseUrl)");

        // Scoped to the factory's own body: the file legitimately constructs bare clients for
        // unrelated handlers, so a whole-file ban would be a false positive.
        var start = source.IndexOf("DefaultMemoryClientFactory(string baseUrl)", StringComparison.Ordinal);
        await Assert.That(start).IsGreaterThan(-1);

        var factoryBody = source.Substring(start, Math.Min(400, source.Length - start));

        await Assert.That(factoryBody).Contains("CreateClientWithAuthStatusAsync");
        await Assert.That(factoryBody).Contains("rejectedAccessToken");
        await Assert.That(factoryBody).DoesNotContain("new HttpClient(");
    }

    // Stop shares the handshake constant but must NEVER carry memory context: it is a
    // per-turn-ish event and injecting there would re-inject on every stop.
    [Test]
    public async Task stop_output_never_carries_memory_context() {
        var sw = new StringWriter();
        CodexHookCommand.WriteSessionScopedOutput(sw);

        await Assert.That(sw.ToString()).DoesNotContain("additionalContext");
        await Assert.That(sw.ToString()).IsEqualTo("""{"continue":true}""");
    }
}
