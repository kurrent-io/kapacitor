using Capacitor.Cli.Core.Pi;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Pins the TypeScript half of the Pi memory contract as text. Each assertion names the load-bearing
/// rule it pins; if one fails after an intentional extension edit, update BOTH halves knowingly.
/// </summary>
public class PiExtensionMemoryTests {
    const string Ts = PiExtensionInstaller.ExtensionContent;

    // The negotiation flag: without it the CLI never fetches and never spends the lease.
    [Test]
    public async Task session_start_declares_the_memory_contract() {
        await Assert.That(Ts).Contains("\"--memory-contract\", \"1\"");
    }

    // Locked representation: systemPrompt append, never a persisted CustomMessage (out-of-scope
    // transcript rewriting). The two assertions together pin "systemPrompt out, message not".
    [Test]
    public async Task injection_is_a_system_prompt_append_not_a_message() {
        await Assert.That(Ts).Contains("before_agent_start");
        await Assert.That(Ts).Contains("systemPrompt:");
        await Assert.That(Ts).DoesNotContain("message:");
    }

    // stdout is only trusted when it opens with the marker — arbitrary stderr-ish noise or a future
    // non-fragment stdout line must not be appended to the model's system prompt verbatim.
    [Test]
    public async Task fragment_is_validated_by_marker_before_caching() {
        await Assert.That(Ts).Contains("<!-- kcap-memory-index:v1 -->");
    }

    // The cache is keyed by the session FILE and consulted against the CURRENT file in
    // before_agent_start — a switched/forked session must not inherit another session's fragment.
    [Test]
    public async Task cache_is_keyed_and_checked_by_session_file() {
        await Assert.That(Ts).Contains("memFile");
    }

    // Idempotence within a turn's chained prompt: appending twice would double the index.
    [Test]
    public async Task injection_is_skipped_when_the_prompt_already_carries_the_fragment() {
        await Assert.That(Ts).Contains("includes(memFragment)");
    }

    // session_shutdown clears the cache: terminal cleanup per the design.
    [Test]
    public async Task shutdown_clears_the_cache() {
        await Assert.That(Ts).Contains("memFragment = null");
    }
}
