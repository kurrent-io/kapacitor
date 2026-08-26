#pragma warning disable IDE0005 // Suppress false positive on necessary usings
using System.Text;
using Capacitor.Cli.Core;
#pragma warning restore IDE0005

namespace Capacitor.Cli.Core.Tests.Unit;

public class JsonlTailTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Complete_lines_are_delivered_once_and_a_partial_final_line_is_held() {
        var path = Tmp.CreateFile("t.jsonl", "{\"a\":1}\n{\"b\":2}\n{\"c\":");
        var tail = new JsonlTail(path);

        var first = tail.ReadAppended();
        await Assert.That(first.Status).IsEqualTo(TailStatus.Ok);
        await Assert.That(first.Lines).IsEquivalentTo(new[] { "{\"a\":1}", "{\"b\":2}" });

        var second = tail.ReadAppended();
        await Assert.That(second.Lines).IsEmpty();

        File.AppendAllText(path, "3}\n");
        var third = tail.ReadAppended();
        await Assert.That(third.Lines).IsEquivalentTo(new[] { "{\"c\":3}" });
    }

    [Test]
    public async Task Crlf_is_stripped_and_blank_lines_are_skipped() {
        var path = Tmp.CreateFile("t.jsonl", "{\"a\":1}\r\n\n   \n{\"b\":2}\r\n");
        var read = new JsonlTail(path).ReadAppended();

        await Assert.That(read.Lines).IsEquivalentTo(new[] { "{\"a\":1}", "{\"b\":2}" });
    }

    [Test]
    public async Task Length_regression_resets_and_rereads_from_zero() {
        var path = Tmp.CreateFile("t.jsonl", "{\"a\":1}\n{\"b\":2}\n");
        var tail = new JsonlTail(path);
        tail.ReadAppended();

        File.WriteAllText(path, "{\"z\":9}\n");
        var read = tail.ReadAppended();

        await Assert.That(read.Status).IsEqualTo(TailStatus.Reset);
        await Assert.That(read.Lines).IsEquivalentTo(new[] { "{\"z\":9}" });
        await Assert.That(tail.Cursor).IsEqualTo(8);
    }

    [Test]
    public async Task Missing_file_is_reported_then_read_once_it_appears() {
        var path = Tmp.PathTo("later.jsonl");
        var tail = new JsonlTail(path);

        await Assert.That(tail.ReadAppended().Status).IsEqualTo(TailStatus.Missing);

        File.WriteAllText(path, "{\"a\":1}\n");
        var read = tail.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(TailStatus.Ok);
        await Assert.That(read.Lines).IsEquivalentTo(new[] { "{\"a\":1}" });
    }

    [Test]
    public async Task A_transient_failure_keeps_the_cursor_and_the_next_read_succeeds() {
        var path = Tmp.CreateFile("t.jsonl", "{\"a\":1}\n");
        var tail = new JsonlTail(path);
        tail.ReadAppended();

        // A directory in the file's place is the one failure every OS reports as neither
        // FileNotFound nor DirectoryNotFound when opened for read.
        File.Delete(path);
        Directory.CreateDirectory(path);
        var failed = tail.ReadAppended();
        await Assert.That(failed.Status).IsEqualTo(TailStatus.Failed);
        await Assert.That(failed.Failure).IsNotNull();
        await Assert.That(tail.Cursor).IsEqualTo(8);

        Directory.Delete(path);
        File.WriteAllText(path, "{\"a\":1}\n{\"b\":2}\n");
        var read = tail.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(TailStatus.Ok);
        await Assert.That(read.Lines).IsEquivalentTo(new[] { "{\"b\":2}" });
    }

    [Test]
    public async Task Reads_a_file_another_handle_holds_open_for_writing() {
        var path = Tmp.PathTo("live.jsonl");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write(Encoding.UTF8.GetBytes("{\"a\":1}\n"));
        writer.Flush();

        var read = new JsonlTail(path).ReadAppended();

        await Assert.That(read.Status).IsEqualTo(TailStatus.Ok);
        await Assert.That(read.Lines).IsEquivalentTo(new[] { "{\"a\":1}" });
    }

    [Test]
    public async Task Split_consumes_only_through_the_last_newline() {
        var bytes = Encoding.UTF8.GetBytes("x\ny\nzz");
        var lines = JsonlTail.SplitCompleteLines(bytes, out var consumed);

        await Assert.That(lines).IsEquivalentTo(new[] { "x", "y" });
        await Assert.That(consumed).IsEqualTo(4);
    }
}
