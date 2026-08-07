using Capacitor.Cli;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.OpenCode;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The OpenCode memory-injection contract, in two halves that have to agree without a compiler to make
/// them: the CLI writes a RAW fragment to stdout, and the generated TypeScript plugin reads it and
/// appends it through <c>experimental.chat.system.transform</c>.
///
/// <para>The plugin half is asserted as TEXT, which is the strongest static check available for
/// generated source — and deliberately not mistaken for proof that OpenCode delivers the fragment to
/// the model. The transform is an EXPERIMENTAL API; only the gated live-cert test
/// (<c>OpenCodeMemoryIndexLiveCertTests</c>) establishes model receipt, and it is the only thing that
/// would notice an upstream change that silently stops delivering.</para>
/// </summary>
public class OpenCodeSessionStartMemoryTests {
    static string Render(string? fragment) => OpenCodeHookCommand.RenderMemoryOutput(fragment);

    // Byte-identical to the pre-feature behaviour on every path a user gets no index: opt-out,
    // exclusion, provider failure, budget exhaustion, and (the common case) a repeat start whose lease
    // is already spent. Load-bearing on this harness specifically, because the plugin treats ANY
    // non-empty stdout as a fragment — a placeholder would have it append an empty system entry to
    // every model request for the rest of the session.
    [Test]
    public async Task no_fragment_writes_zero_bytes() {
        await Assert.That(Render(null)).IsEqualTo("");
    }

    // Raw text, no envelope — the inverse of Antigravity's JSON shape, so it is pinned rather than
    // assumed. The plugin trims, so the terminator is cosmetic; the absence of an envelope is not.
    [Test]
    public async Task a_fragment_is_written_raw_with_a_single_terminator() {
        await Assert.That(Render("F")).IsEqualTo("F\n");
    }

    // Markdown carries newlines, quotes and backslashes. With no envelope there is nothing to escape,
    // so the fragment must survive byte-for-byte — the property the plugin depends on when it matches
    // the marker.
    [Test]
    [Arguments("quote \" backslash \\ text")]
    [Arguments("- item one\n- item two\n\n### Heading")]
    [Arguments("non-BMP \U0001F600 emoji")]
    public async Task a_fragment_round_trips_verbatim(string fragment) {
        await Assert.That(Render(fragment)).IsEqualTo(fragment + "\n");
    }

    [Test]
    public async Task Lifecycle_is_a_repeating_top_level_callback() {
        var lifecycle = OpenCodeHookCommand.LifecycleFor("ses023575b3cffetNkaAklu6CAtNp");

        await Assert.That(lifecycle.Harness).IsEqualTo(SessionStartHarness.OpenCode);
        // The plugin only invokes the CLI for a session its classifier has PROVEN top-level; it defers
        // on ambiguous parentage rather than guessing, so a child never reaches here.
        await Assert.That(lifecycle.IsTopLevel).IsTrue();
        await Assert.That(lifecycle.ClassificationAuthoritative).IsTrue();
        await Assert.That(lifecycle.Reason).IsEqualTo(SessionLifecycleReason.RepeatedTurnCallback);
        // The plugin's start dedupe is per PROCESS: a restart or a resumed session fires it again for
        // the same id, and only the durable lease makes that a no-op. EligibleOneShot here would
        // re-inject once per opencode restart.
        await Assert.That(lifecycle.CallbackMayRepeat).IsTrue();

        var decision = SessionStartMemoryLifecyclePolicy.Decide(lifecycle);
        await Assert.That(decision).IsEqualTo(SessionMemoryLifecycleDecision.EligibleWithLease);
    }

    /// <summary>
    /// The cross-runtime negotiation the epic specifies. Absence means contract 0, so a NEW binary
    /// paired with an already-installed OLDER plugin fetches nothing and spends no lease — that plugin
    /// discards this command's stdout, and the lease is what makes injection once-per-session, so
    /// spending it for a caller that cannot deliver is the thing worth negotiating about.
    /// </summary>
    [Test]
    [Arguments(new[] { "--event", "session-start" }, 0)]                                    // older plugin
    [Arguments(new[] { "--event", "session-start", "--memory-contract", "1" }, 1)]
    [Arguments(new[] { "--event", "session-start", "--memory-contract", "2" }, 2)]          // a future one
    [Arguments(new[] { "--event", "session-start", "--memory-contract", "abc" }, 0)]        // unparseable
    [Arguments(new[] { "--event", "session-start", "--memory-contract" }, 0)]               // no value
    public async Task the_memory_contract_version_defaults_to_zero_when_undeclared(
            string[] args, int expected) {
        await Assert.That(OpenCodeHookCommand.MemoryContractOf(args)).IsEqualTo(expected);
    }

    /// <summary>
    /// The generated plugin must DECLARE the contract, or a new binary paired with it fetches nothing
    /// and the feature is silently inert.
    ///
    /// <para>The lifecycle arguments are asserted alongside it because the flag was ADDED to an existing
    /// array: dropping `--session` or `--file` while adding it would break CAPTURE — the watcher spawn
    /// and the lifecycle POST — which no memory test would notice, and which is a far worse regression
    /// than losing the index. (Verified live too: with no contract flag the hook writes zero bytes and
    /// still spawns the watcher.)</para>
    /// </summary>
    [Test]
    public async Task the_plugin_declares_the_memory_contract_without_dropping_the_lifecycle_args() {
        var content = OpenCodeExtensionInstaller.ExtensionContent;

        await Assert.That(content).Contains("\"--memory-contract\", \"1\"");

        // The pre-existing vector capture depends on.
        await Assert.That(content).Contains("\"hook\", \"--opencode\", \"--event\", \"session-start\"");
        await Assert.That(content).Contains("\"--session\", sid");
        await Assert.That(content).Contains("\"--file\", file(sid)");
        await Assert.That(content).Contains("args.push(\"--cwd\", String(cwd))");
    }

    /// <summary>
    /// stdout is a data channel, so it needs a shape. Only output opening with the marker is treated as
    /// a fragment — otherwise any line some future code path prints there would be appended to the
    /// model's system prompt verbatim.
    /// </summary>
    [Test]
    public async Task the_plugin_validates_the_marker_before_trusting_stdout() {
        await Assert.That(OpenCodeExtensionInstaller.ExtensionContent)
            .Contains("if (!fragment.startsWith(MEMORY_MARKER)) return");
    }

    /// <summary>
    /// The marker is deliberately NOT stripped before injection, which is a documented deviation from
    /// the epic's cross-runtime contract text. It is the only way to recognise an already-appended
    /// fragment in a system array this plugin does not own — the guard that keeps injection correct if a
    /// future OpenCode retains transformed entries instead of rebuilding them — and it is an invisible
    /// HTML comment, so leaving it in costs a reader nothing. Pinned so the deviation is deliberate
    /// rather than forgotten.
    /// </summary>
    [Test]
    public async Task the_marker_is_retained_in_the_injected_fragment() {
        // The CLI does not strip it on the way out...
        await Assert.That(Render("F")).IsEqualTo("F\n");
        await Assert.That(MemoryIndexEmitter.FragmentMarker).StartsWith("<!--");

        // ...and the plugin pushes the fragment as-is rather than a substring of it.
        await Assert.That(OpenCodeExtensionInstaller.ExtensionContent).Contains("system.push(fragment)");
        await Assert.That(OpenCodeExtensionInstaller.ExtensionContent)
            .DoesNotContain("fragment.replace(MEMORY_MARKER");
        await Assert.That(OpenCodeExtensionInstaller.ExtensionContent)
            .DoesNotContain("fragment.slice(MEMORY_MARKER.length)");
    }

    /// <summary>
    /// The two halves' shared literal. The CLI emits this marker at the head of every fragment and the
    /// plugin recognises an already-appended fragment by it; they live in different languages, so
    /// nothing but this assertion makes them agree.
    /// </summary>
    [Test]
    public async Task the_plugin_recognises_the_marker_the_emitter_writes() {
        await Assert.That(OpenCodeExtensionInstaller.ExtensionContent)
            .Contains(MemoryIndexEmitter.FragmentMarker);
    }

    /// <summary>
    /// The hook name is an EXPERIMENTAL API and a literal string OpenCode looks up by exact key on the
    /// object the plugin returns — a typo registers nothing at all, silently, with every other test
    /// still passing.
    /// </summary>
    [Test]
    public async Task the_plugin_registers_the_system_transform_hook() {
        await Assert.That(OpenCodeExtensionInstaller.ExtensionContent)
            .Contains("\"experimental.chat.system.transform\"");
    }

    /// <summary>
    /// Append, never replace. <c>system[0]</c> is OpenCode's whole assembled prompt, and OpenCode's own
    /// normalisation of the array is conditional on <c>system[0]</c> being unchanged — so an assignment
    /// into the array would both drop the real prompt and defeat that normalisation.
    /// </summary>
    [Test]
    public async Task the_plugin_appends_to_the_system_array_and_never_assigns_into_it() {
        var content = OpenCodeExtensionInstaller.ExtensionContent;

        await Assert.That(content).Contains("system.push(fragment)");
        await Assert.That(content).DoesNotContain("system[0] =");
        await Assert.That(content).DoesNotContain("output.system =");
        await Assert.That(content).DoesNotContain("system.length = 0");
    }

    /// <summary>
    /// The guard that keeps injection out of OpenCode's agent-config GENERATOR, which triggers the same
    /// hook name with no <c>sessionID</c>. Without it, every generated agent definition would carry the
    /// team-memory index in its system prompt.
    /// </summary>
    [Test]
    public async Task the_plugin_declines_to_inject_without_a_session_id() {
        await Assert.That(OpenCodeExtensionInstaller.ExtensionContent)
            .Contains("if (!sid) return");
    }

    /// <summary>
    /// A subagent must never receive the parent's fragment through plugin state. The classifier's own
    /// <c>children</c> set is the authority, and the transform consults it.
    /// </summary>
    [Test]
    public async Task the_plugin_declines_to_inject_into_a_known_child_session() {
        await Assert.That(OpenCodeExtensionInstaller.ExtensionContent)
            .Contains("if (children.has(sid)) return          // (2)");
    }

    /// <summary>
    /// The plugin state must be bounded — a process-global map keyed by session id grows for the life of
    /// a long-running OpenCode process otherwise.
    /// </summary>
    [Test]
    public async Task the_plugin_bounds_its_cached_state() {
        var content = OpenCodeExtensionInstaller.ExtensionContent;

        await Assert.That(content).Contains("MEMORY_MAX_SESSIONS");
        await Assert.That(content).Contains("memory.size >= MEMORY_MAX_SESSIONS");
        // The prompt path for a deleted session is tidied rather than left to eviction.
        await Assert.That(content).Contains("memory.delete(sid)");
    }

    /// <summary>
    /// OpenCode awaits this hook WITHOUT a try/catch of its own, so a throw here surfaces as a failed
    /// model request — the one failure mode a fail-open feature must not have.
    /// </summary>
    [Test]
    public async Task the_plugin_transform_cannot_throw() {
        var content = OpenCodeExtensionInstaller.ExtensionContent;
        var start   = content.IndexOf("\"experimental.chat.system.transform\"", StringComparison.Ordinal);
        var end     = content.IndexOf("event: async", StringComparison.Ordinal);

        await Assert.That(start).IsGreaterThan(-1);
        await Assert.That(end).IsGreaterThan(start);

        var transform = content[start..end];

        await Assert.That(transform).Contains("try {");
        await Assert.That(transform).Contains("} catch {");
    }

    /// <summary>
    /// A repeated start that returns nothing must not erase a fragment already cached — the common case
    /// once the durable lease has been spent, where the CLI legitimately prints zero bytes.
    /// </summary>
    [Test]
    public async Task the_plugin_never_overwrites_a_cached_fragment_with_an_empty_one() {
        var content = OpenCodeExtensionInstaller.ExtensionContent;

        await Assert.That(content).Contains("if (!fragment) return");
        await Assert.That(content).Contains("if (memory.has(sid)) return");
    }

    /// <summary>
    /// The race that the live cert caught and unit tests could not: the in-flight marker must be
    /// published by <c>ensureStarted</c> — whose body sets it before its first <c>await</c> — and NOT
    /// from inside <c>start</c>, which only runs after classification resolves.
    ///
    /// <para>OpenCode publishes <c>session.created</c> and then issues the first LLM request, so a
    /// marker set after the classify leaves a window in which the transform sees no pending start and
    /// declines to inject. On a one-turn run that means the session never gets its index at all — and
    /// because it is a race it presents as intermittent, which is why it survived the whole unit suite.
    /// This test is a cheap structural guard for the shape; the cert remains the real proof.</para>
    /// </summary>
    [Test]
    public async Task the_plugin_publishes_the_in_flight_marker_before_classifying() {
        var content = OpenCodeExtensionInstaller.ExtensionContent;

        await Assert.That(content).Contains("function ensureStarted(");
        // The transform awaits the marker ensureStarted owns...
        await Assert.That(content).Contains("pendingStart.get(sid)");
        // ...and both event paths go through it, so no path starts a session without publishing one.
        await Assert.That(content).Contains("await ensureStarted(sid, event.properties?.info)");
        await Assert.That(content).Contains("if (!(await ensureStarted(sid))) return");
        // start() must NOT manage the marker itself — that is precisely the defect.
        await Assert.That(content).DoesNotContain("starting.set(");
    }

    /// <summary>
    /// The request-path healing must be BOUNDED. A session whose classification keeps failing never
    /// reaches <c>start()</c>, so <c>started</c> never records it and the transform would re-trigger
    /// <c>classify</c> on every model request for the life of the process. Found by review tracing the
    /// <c>!started.has(sid)</c> guard; the event path's own retry (session.idle) is unaffected.
    /// </summary>
    [Test]
    public async Task the_plugin_bounds_request_path_cold_start_retries() {
        var content = OpenCodeExtensionInstaller.ExtensionContent;

        await Assert.That(content).Contains("MEMORY_COLD_START_ATTEMPTS");
        await Assert.That(content).Contains("attempts < MEMORY_COLD_START_ATTEMPTS");
        await Assert.That(content).Contains("coldStarts.set(sid, attempts + 1)");
        // Cleared with the session, so a long-lived process does not accumulate counters.
        await Assert.That(content).Contains("coldStarts.delete(sid)");
    }

    /// <summary>
    /// stdout became a DATA channel, so the start path must actually READ it. `.quiet()` alone would
    /// discard the fragment while every other test still passed.
    /// </summary>
    [Test]
    public async Task the_plugin_captures_the_hook_stdout() {
        var content = OpenCodeExtensionInstaller.ExtensionContent;

        await Assert.That(content).Contains("res?.stdout");
        await Assert.That(content).Contains("rememberMemory(sid, (await runKcap(args)).trim())");
    }
}
