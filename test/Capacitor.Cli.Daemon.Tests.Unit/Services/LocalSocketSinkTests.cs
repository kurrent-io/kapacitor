using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class LocalSocketSinkTests {
    [Test]
    public async Task Delivers_chunks_in_order() {
        var got = new List<string>();
        var sink = new LocalSocketSink(capacity: 8, (b, _) => { got.Add(Encoding.UTF8.GetString(b)); return Task.CompletedTask; });
        var run = sink.RunAsync(default);
        foreach (var s in new[] { "a", "b", "c" }) sink.TryEnqueue(Encoding.UTF8.GetBytes(s));
        sink.Complete();
        await run;
        await Assert.That(got).IsEquivalentTo(new[] { "a", "b", "c" });
    }

    [Test]
    public async Task Overflow_marks_detached_and_never_blocks_producer() {
        // writer never drains until released; tiny capacity → overflow on the producer side
        var tcs  = new TaskCompletionSource();
        var sink = new LocalSocketSink(capacity: 2, async (_, c) => await tcs.Task.WaitAsync(c));
        var run  = sink.RunAsync(default);

        for (var i = 0; i < 1000; i++) sink.TryEnqueue([1]); // must not block

        await Assert.That(sink.Detached).IsTrue();

        tcs.SetResult();
        sink.Complete();
    }
}
