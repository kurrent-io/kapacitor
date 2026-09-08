using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class TranscriptBatchBufferTests {
    [Test]
    public async Task Fills_at_the_line_cap() {
        var buffer = new TranscriptBatchBuffer();

        for (var i = 0; i < TranscriptBatchBuffer.MaxLines - 1; i++) buffer.Add("x", i, 1);

        await Assert.That(buffer.IsFull).IsFalse();

        buffer.Add("x", TranscriptBatchBuffer.MaxLines - 1, 1);

        await Assert.That(buffer.IsFull).IsTrue();
        await Assert.That(buffer.FirstLineNumber).IsEqualTo(0);
        await Assert.That(buffer.LastLineNumber).IsEqualTo(TranscriptBatchBuffer.MaxLines - 1);
    }

    [Test]
    public async Task Refuses_a_line_that_would_pass_the_byte_budget() {
        var buffer = new TranscriptBatchBuffer();
        buffer.Add("big", 0, 3 * 1024 * 1024);

        await Assert.That(buffer.Fits(1024 * 1024)).IsTrue();
        await Assert.That(buffer.Fits(1024 * 1024 + 1)).IsFalse();

        buffer.Clear();

        await Assert.That(buffer.IsEmpty).IsTrue();
        await Assert.That(buffer.Fits(TranscriptBatchBuffer.MaxBytes)).IsTrue();
    }

    [Test]
    public async Task Measures_a_line_in_utf8_bytes() {
        await Assert.That(TranscriptBatchBuffer.SizeOf("x")).IsEqualTo(1);
        await Assert.That(TranscriptBatchBuffer.SizeOf("é")).IsEqualTo(2);
        await Assert.That(TranscriptBatchBuffer.SizeOf("日")).IsEqualTo(3);
    }
}
