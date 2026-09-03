namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using System.Text.Json;
using Capacitor.Cli.Core.Policy;

public class PolicyDecisionJournalTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    PolicyDecisionJournal Journal => new(Config.Root);
    const string Sid = "abc123";

    [Test]
    public async Task Fallback_ask_is_fifo_per_input_hash_and_consume_once() {
        Journal.RecordAsk(Sid, callId: null, inputHash: "h1");
        Journal.RecordAsk(Sid, callId: null, inputHash: "h1");
        Journal.RecordAsk(Sid, callId: null, inputHash: "h2");
        var first = Journal.Consume(Sid, callId: null, inputHash: "h1");
        await Assert.That(first.PendingAsk).IsTrue();
        await Assert.That(first.Ambiguous).IsTrue();
        await Assert.That(Journal.Consume(Sid, null, "h1").PendingAsk).IsTrue();
        await Assert.That(Journal.Consume(Sid, null, "h1").PendingAsk).IsFalse();
        await Assert.That(Journal.Consume(Sid, null, "h2").PendingAsk).IsTrue();
    }

    [Test]
    public async Task Exact_mode_journals_all_terminals_with_exact_provenance() {
        Journal.RecordTerminal(Sid, "call-1", "deny", "h1");
        Journal.RecordAsk(Sid, "call-2", "h2");
        var deny = Journal.Consume(Sid, "call-1", "h1");
        await Assert.That(deny.ExactOutcome).IsEqualTo("deny");
        await Assert.That(deny.Ambiguous).IsFalse();
        await Assert.That(deny.PendingAsk).IsFalse();
        var ask = Journal.Consume(Sid, "call-2", "h2");
        await Assert.That(ask.PendingAsk).IsTrue();
        await Assert.That(ask.Ambiguous).IsFalse();
        await Assert.That(Journal.Consume(Sid, "call-1", "h1").ExactOutcome).IsNull();
    }

    [Test]
    public async Task Unknown_call_id_with_no_pending_hash_is_an_ordinary_fresh_call() {
        var r = Journal.Consume(Sid, "never-seen", "h9");
        await Assert.That(r).IsEqualTo(default(PolicyJournalConsume));
    }

    [Test]
    public async Task Clear_turn_expires_entries_but_keeps_the_pass_through_count() {
        Journal.RecordAsk(Sid, null, "h1");
        Journal.IncrementPassThrough(Sid);
        Journal.IncrementPassThrough(Sid);
        Journal.ClearTurn(Sid);
        await Assert.That(Journal.Consume(Sid, null, "h1").PendingAsk).IsFalse();
        await Assert.That(Journal.TakePassThroughCount(Sid)).IsEqualTo(2);
        await Assert.That(Journal.TakePassThroughCount(Sid)).IsEqualTo(0);
    }

    /// <summary>`stop` fires every turn of every session, so a clear with nothing to expire must not
    /// mint a journal for a session that never journaled anything.</summary>
    [Test]
    public async Task Clear_turn_writes_nothing_for_a_session_with_no_journal() {
        Journal.ClearTurn(Sid);
        await Assert.That(Directory.Exists(Config.Root.Path("policy", "journal"))).IsFalse();
    }

    [Test]
    public async Task Sessions_are_isolated() {
        Journal.RecordAsk("s1", null, "h1");
        await Assert.That(Journal.Consume("s2", null, "h1").PendingAsk).IsFalse();
        await Assert.That(Journal.Consume("s1", null, "h1").PendingAsk).IsTrue();
    }

    [Test]
    public async Task A_parsable_but_incomplete_journal_file_does_not_throw() {
        var dir = Config.Root.Path("policy", "journal");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{Sid}.json"), """{"pass_through_count":3}""");
        await Assert.That(Journal.Consume(Sid, null, "h1")).IsEqualTo(default(PolicyJournalConsume));
    }

    [Test]
    public async Task A_later_terminal_replaces_an_earlier_ask_for_the_same_call_id() {
        Journal.RecordAsk(Sid, "call-1", "h1");
        Journal.RecordTerminal(Sid, "call-1", "deny", "h1");
        var r = Journal.Consume(Sid, "call-1", "h1");
        await Assert.That(r.ExactOutcome).IsEqualTo("deny");
        await Assert.That(r.PendingAsk).IsFalse();
        await Assert.That(Journal.Consume(Sid, "call-1", "h1")).IsEqualTo(default(PolicyJournalConsume));
    }

    [Test]
    public async Task RecordTerminal_with_no_call_id_does_nothing() {
        Journal.RecordTerminal(Sid, callId: "", outcome: "deny", inputHash: "h1");
        await Assert.That(Journal.Consume(Sid, "", "h1")).IsEqualTo(default(PolicyJournalConsume));
    }

    [Test]
    public async Task Consume_with_a_missing_call_id_falls_back_to_a_hitting_fifo_hash() {
        Journal.RecordAsk(Sid, callId: null, inputHash: "h1");
        var r = Journal.Consume(Sid, "never-recorded", "h1");
        await Assert.That(r.PendingAsk).IsTrue();
        await Assert.That(r.ExactOutcome).IsNull();
        await Assert.That(r.Ambiguous).IsTrue();
    }

    [Test]
    public async Task An_exact_entry_takes_precedence_and_leaves_the_fifo_hash_intact() {
        Journal.RecordAsk(Sid, callId: null, inputHash: "h1");
        Journal.RecordTerminal(Sid, "call-1", "allow", "h1");
        var exact = Journal.Consume(Sid, "call-1", "h1");
        await Assert.That(exact.ExactOutcome).IsEqualTo("allow");
        await Assert.That(exact.Ambiguous).IsFalse();
        var fifo = Journal.Consume(Sid, null, "h1");
        await Assert.That(fifo.PendingAsk).IsTrue();
        await Assert.That(fifo.Ambiguous).IsTrue();
    }

    [Test]
    public async Task An_ask_recorded_with_a_call_id_is_not_consumable_by_hash_alone() {
        Journal.RecordAsk(Sid, "call-1", "h1");
        await Assert.That(Journal.Consume(Sid, callId: null, inputHash: "h1")).IsEqualTo(default(PolicyJournalConsume));
    }
}

public class PolicyInputHashTests {
    static JsonElement Input(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task Key_order_does_not_change_the_hash() {
        var a = PolicyInputHash.Compute("Bash", Input("""{"command":"ls","description":"x"}"""));
        var b = PolicyInputHash.Compute("Bash", Input("""{"description":"x","command":"ls"}"""));
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task Tool_name_and_values_do() {
        var a = PolicyInputHash.Compute("Bash", Input("""{"command":"ls"}"""));
        await Assert.That(PolicyInputHash.Compute("Edit", Input("""{"command":"ls"}"""))).IsNotEqualTo(a);
        await Assert.That(PolicyInputHash.Compute("Bash", Input("""{"command":"rm"}"""))).IsNotEqualTo(a);
        await Assert.That(PolicyInputHash.Compute("Bash", null)).IsNotEqualTo(a);
    }

    [Test]
    public async Task String_escaping_does_not_change_the_hash() {
        var a = PolicyInputHash.Compute("Bash", Input("""{"a":"A"}"""));
        var unicodeEscaped = "{\"a\":\"" + "\\u0041" + "\"}";
        var b = PolicyInputHash.Compute("Bash", Input(unicodeEscaped));
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task Number_spelling_does_not_change_the_hash() {
        var a = PolicyInputHash.Compute("Bash", Input("""{"n":1}"""));
        var b = PolicyInputHash.Compute("Bash", Input("""{"n":1.0}"""));
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task Nested_key_order_does_not_change_the_hash() {
        var a = PolicyInputHash.Compute("Bash", Input("""{"a":{"b":1,"c":2}}"""));
        var b = PolicyInputHash.Compute("Bash", Input("""{"a":{"c":2,"b":1}}"""));
        await Assert.That(a).IsEqualTo(b);
    }
}
