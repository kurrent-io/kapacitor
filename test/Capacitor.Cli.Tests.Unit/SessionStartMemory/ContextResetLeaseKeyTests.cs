using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// <c>/clear</c> destroys the model context holding the injected index, but the lease is keyed per session,
/// so the re-fired SessionStart found a completed lease and injected nothing — the index was silently gone
/// for the rest of the session.
///
/// <para>These assert SEQUENCES, not states. A state-only suite passes against an implementation that
/// always injects, which is the failure mode in the other direction.</para>
///
/// <para>Claude is the harness named throughout because it is the one that passes a discriminator. Gemini
/// deliberately does not: read from gemini-cli 0.53.0, its `/clear` mints a NEW session id before firing
/// the hook — so the session id is already the generation — and its handler consumes only
/// `systemMessage`, never the `additionalContext` this adapter delivers through.</para>
/// </summary>
internal class ContextResetLeaseKeyTests {
    const string Session = "3f2a1b4c5d6e7f8091a2b3c4d5e6f708";

    static string KeyFor(SessionStartHarness harness, string? instanceId) =>
        SessionStartMemoryIdentity.Create(harness, Session, instanceId);

    // ── the migration property, which is what made this safe to ship ──

    /// <summary>
    /// THE regression guard for the five already-merged adapters. Generation zero is spelled as a null
    /// instance id, which is exactly what a pre-generation CLI wrote — so a newer hook firing into a session
    /// started under the old one computes the same key, finds the completed lease, and stays silent. If this
    /// fails, every shipped adapter re-injects on its next lifecycle event.
    /// </summary>
    [Test]
    [Arguments(SessionLifecycleReason.New)]
    [Arguments(SessionLifecycleReason.Resume)]
    [Arguments(SessionLifecycleReason.Reopen)]
    [Arguments(SessionLifecycleReason.Fork)]
    public async Task A_non_reset_reason_keeps_the_legacy_lease_key(SessionLifecycleReason reason) {
        var instanceId = SessionStartMemoryHookSupport.ContextResetInstanceId(reason, "any-discriminator");

        await Assert.That(instanceId).IsNull();
        await Assert.That(KeyFor(SessionStartHarness.Claude, instanceId))
            .IsEqualTo(KeyFor(SessionStartHarness.Claude, null));
    }

    // ── the sequences from the acceptance criteria ──

    /// <summary>startup → resume: ONE injection. The resume must reuse the startup key.</summary>
    [Test]
    public async Task Startup_then_resume_yields_one_lease_key() {
        var startup = SessionStartMemoryHookSupport.ContextResetInstanceId(SessionLifecycleReason.New, "t0");
        var resume  = SessionStartMemoryHookSupport.ContextResetInstanceId(SessionLifecycleReason.Resume, "t1");

        await Assert.That(KeyFor(SessionStartHarness.Claude, resume))
            .IsEqualTo(KeyFor(SessionStartHarness.Claude, startup));
    }

    /// <summary>startup → clear: a SECOND key, so the index is injected again.</summary>
    [Test]
    public async Task Startup_then_clear_yields_a_second_lease_key() {
        var startup = SessionStartMemoryHookSupport.ContextResetInstanceId(SessionLifecycleReason.New, "t0");
        var cleared = SessionStartMemoryHookSupport.ContextResetInstanceId(SessionLifecycleReason.Clear, "t1");

        await Assert.That(KeyFor(SessionStartHarness.Claude, cleared))
            .IsNotEqualTo(KeyFor(SessionStartHarness.Claude, startup));
    }

    /// <summary>clear → clear: a THIRD key. Two genuine clears must both inject.</summary>
    [Test]
    public async Task Two_clears_yield_three_distinct_lease_keys() {
        var keys = new[] {
            KeyFor(SessionStartHarness.Claude,
                SessionStartMemoryHookSupport.ContextResetInstanceId(SessionLifecycleReason.New, "t0")),
            KeyFor(SessionStartHarness.Claude,
                SessionStartMemoryHookSupport.ContextResetInstanceId(SessionLifecycleReason.Clear, "t1")),
            KeyFor(SessionStartHarness.Claude,
                SessionStartMemoryHookSupport.ContextResetInstanceId(SessionLifecycleReason.Clear, "t2"))
        };

        await Assert.That(keys.Distinct().Count()).IsEqualTo(3);
    }

    /// <summary>
    /// The dedupe half, and the reason Gemini's timestamp is a usable id at all: a REDELIVERY of one clear
    /// carries the identical payload, so it must collapse to the same key and be absorbed by the lease. A
    /// per-invocation id would inject twice here.
    /// </summary>
    [Test]
    public async Task A_redelivered_clear_with_the_same_discriminator_collapses_to_one_key() {
        var first  = SessionStartMemoryHookSupport.ContextResetInstanceId(SessionLifecycleReason.Clear, "t1");
        var repeat = SessionStartMemoryHookSupport.ContextResetInstanceId(SessionLifecycleReason.Clear, "t1");

        await Assert.That(KeyFor(SessionStartHarness.Claude, repeat))
            .IsEqualTo(KeyFor(SessionStartHarness.Claude, first));
    }

    // ── policy ──

    /// <summary>A clear with no discriminator is SUPPRESSED, not honoured. Honouring it would re-inject on
    /// every later lifecycle event rather than once per clear, because nothing distinguishes it from the
    /// session start that already injected.</summary>
    [Test]
    public async Task A_clear_without_a_discriminator_is_ineligible() {
        var decision = SessionStartMemoryLifecyclePolicy.Decide(
            new SessionMemoryLifecycle(SessionStartHarness.Claude, Session, LifecycleInstanceId: null,
                IsTopLevel: true, ClassificationAuthoritative: true,
                SessionLifecycleReason.Clear, CallbackMayRepeat: false));

        await Assert.That(decision).IsEqualTo(SessionMemoryLifecycleDecision.IneligibleNoCommit);
    }

    [Test]
    public async Task A_clear_with_a_discriminator_is_eligible() {
        var decision = SessionStartMemoryLifecyclePolicy.Decide(
            new SessionMemoryLifecycle(SessionStartHarness.Claude, Session,
                SessionStartMemoryHookSupport.ContextResetInstanceId(SessionLifecycleReason.Clear, "t1"),
                IsTopLevel: true, ClassificationAuthoritative: true,
                SessionLifecycleReason.Clear, CallbackMayRepeat: false));

        await Assert.That(decision).IsEqualTo(SessionMemoryLifecycleDecision.EligibleOneShot);
    }

    /// <summary>
    /// The discriminator must be CANONICAL, not the raw payload string. Two spellings of one instant —
    /// a redelivery formatted differently, or with incidental whitespace — would otherwise produce
    /// different lease keys and re-inject, losing the exactly-once property the timestamp exists to give.
    /// The Gemini adapter parses and round-trips it for this reason.
    /// </summary>
    [Test]
    public async Task Equivalent_timestamp_spellings_produce_one_lease_key() {
        // Same instant, three spellings the host could plausibly emit.
        var spellings = new[] { "2026-08-02T10:30:00.0000000+00:00", "2026-08-02T10:30:00Z", " 2026-08-02T10:30:00Z " };

        var keys = spellings
            .Select(raw => DateTimeOffset.Parse(raw.Trim(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind).ToString("O"))
            .Select(canonical => KeyFor(SessionStartHarness.Claude,
                        SessionStartMemoryHookSupport.ContextResetInstanceId(
                            SessionLifecycleReason.Clear, canonical)))
            .Distinct()
            .ToArray();

        await Assert.That(keys.Length).IsEqualTo(1);
    }

    // ── the mapper both harnesses now share ──

    /// <summary><c>clear</c> previously fell through to the catch-all — New for Claude's local copy of the
    /// mapper, Unknown for the shared one — which is why both harnesses had the bug.</summary>
    [Test]
    public async Task The_shared_mapper_recognises_clear() {
        await Assert.That(SessionStartMemoryHookSupport.ReasonFor("clear"))
            .IsEqualTo(SessionLifecycleReason.Clear);
        await Assert.That(SessionStartMemoryHookSupport.ReasonFor("CLEAR"))
            .IsEqualTo(SessionLifecycleReason.Clear);
        // An unrecognised source must still be Unknown, not silently treated as a reset.
        await Assert.That(SessionStartMemoryHookSupport.ReasonFor("something-new"))
            .IsEqualTo(SessionLifecycleReason.Unknown);
    }

    /// <summary>A compact is NOT a context reset — the host keeps the conversation, so re-injecting would
    /// duplicate an index the model can still see.</summary>
    [Test]
    public async Task Compact_is_not_treated_as_a_context_reset() {
        await Assert.That(SessionStartMemoryHookSupport.ReasonFor("compact"))
            .IsEqualTo(SessionLifecycleReason.Compact);
        await Assert.That(SessionStartMemoryHookSupport
                .ContextResetInstanceId(SessionLifecycleReason.Compact, "t1"))
            .IsNull();
    }
}
