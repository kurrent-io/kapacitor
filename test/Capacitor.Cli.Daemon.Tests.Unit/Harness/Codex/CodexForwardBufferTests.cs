using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// The §2.4 forward buffer: canonical envelopes are never dropped (they block the emitter when full),
/// ephemeral envelopes drop when full, and a canonical stall past the timeout fires the terminal fault
/// exactly once.
/// </summary>
public class CodexForwardBufferTests {
    static AcpEventEnvelope Canonical(string text) => new(Kind: AcpEventKind.AssistantText, Text: text);
    static AcpEventEnvelope Ephemeral(string text) =>
        new(Kind: AcpEventKind.AssistantText, Text: text, Ephemeral: true, ItemId: "i1");

    static CodexForwardBuffer New(int capacity, TimeSpan stall, Action<TimeSpan>? onStall = null) =>
        new(capacity, stall, CancellationToken.None, onStall ?? (_ => { }));

    [Test]
    public async Task Canonical_envelopes_are_delivered_in_order() {
        using var buf = New(8, TimeSpan.FromSeconds(5));
        buf.Emit(Canonical("a"));
        buf.Emit(Canonical("b"));
        buf.Emit(Canonical("c"));
        buf.Complete();

        var texts = new List<string?>();
        await foreach (var e in buf.Reader.ReadAllAsync()) texts.Add(e.Text);
        await Assert.That(string.Join(",", texts)).IsEqualTo("a,b,c");
    }

    [Test]
    public async Task Ephemeral_is_dropped_when_full_but_canonical_is_retained() {
        using var buf = New(capacity: 2, TimeSpan.FromSeconds(5));
        buf.Emit(Canonical("c1"));   // fills 1/2
        buf.Emit(Canonical("c2"));   // fills 2/2 → full
        buf.Emit(Ephemeral("live")); // full → dropped

        await Assert.That(buf.DroppedEphemeralCount).IsEqualTo(1);

        // Drain: the two canonical envelopes survived, the ephemeral did not.
        buf.Complete();
        var texts = new List<string?>();
        await foreach (var e in buf.Reader.ReadAllAsync()) texts.Add(e.Text);
        await Assert.That(string.Join(",", texts)).IsEqualTo("c1,c2");
    }

    [Test]
    public async Task Ephemeral_passes_through_when_there_is_room() {
        using var buf = New(8, TimeSpan.FromSeconds(5));
        buf.Emit(Ephemeral("partial"));
        buf.Complete();

        var read = await buf.Reader.ReadAsync();
        await Assert.That(read.Ephemeral).IsTrue();
        await Assert.That(read.Text).IsEqualTo("partial");
        await Assert.That(buf.DroppedEphemeralCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_full_buffer_blocks_a_canonical_emit_until_the_reader_drains() {
        using var buf = New(capacity: 1, TimeSpan.FromSeconds(30));
        buf.Emit(Canonical("first")); // fills 1/1

        var second = Task.Run(() => buf.Emit(Canonical("second"))); // blocks: buffer full
        await Task.Delay(100);
        await Assert.That(second.IsCompleted).IsFalse(); // still blocked — canonical never dropped

        var a = await buf.Reader.ReadAsync();             // drain one → frees a slot
        await second;                                     // the blocked emit now completes
        var b = await buf.Reader.ReadAsync();

        await Assert.That(a.Text).IsEqualTo("first");
        await Assert.That(b.Text).IsEqualTo("second");
        await Assert.That(buf.Stalled).IsFalse();
    }

    [Test]
    public async Task A_canonical_stall_past_the_timeout_fires_the_fault_once_and_stops_accepting() {
        var stalls = new List<TimeSpan>();
        using var buf = New(capacity: 1, TimeSpan.FromMilliseconds(150), stalls.Add);
        buf.Emit(Canonical("first")); // fills 1/1; no reader will ever drain

        // This canonical emit blocks (full) and stalls out after the timeout, firing the fault.
        var blocked = Task.Run(() => buf.Emit(Canonical("stalled")));
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(buf.Stalled).IsTrue();
        await Assert.That(stalls.Count).IsEqualTo(1);

        // Once stalled, further emits are no-ops (the agent is faulting).
        buf.Emit(Canonical("after"));
        buf.Emit(Ephemeral("after"));
        await Assert.That(buf.DroppedEphemeralCount).IsEqualTo(0); // not even counted — buffer is dead
    }
}
