using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class DetachScannerTests {
    [Test]
    public async Task Forwards_normal_bytes_unchanged() {
        var s = new DetachScanner();
        var (forward, detach) = s.Process("hello"u8);
        await Assert.That(detach).IsFalse();
        await Assert.That(forward).IsEquivalentTo("hello"u8.ToArray());
    }

    [Test]
    public async Task Detects_prefix_then_d_split_across_reads() {
        var s = new DetachScanner();

        var (f1, d1) = s.Process([0x11]); // prefix alone — held, nothing forwarded yet
        await Assert.That(d1).IsFalse();
        await Assert.That(f1).IsEmpty();

        var (_, d2) = s.Process([(byte)'d']); // completes the sequence
        await Assert.That(d2).IsTrue();
    }

    [Test]
    public async Task Detects_prefix_then_d_in_one_read() {
        var s = new DetachScanner();
        var (_, detach) = s.Process([0x11, (byte)'d']);
        await Assert.That(detach).IsTrue();
    }

    [Test]
    public async Task Prefix_not_followed_by_d_is_forwarded() {
        var s = new DetachScanner();
        var (forward, detach) = s.Process([0x11, (byte)'x']);
        await Assert.That(detach).IsFalse();
        await Assert.That(forward).IsEquivalentTo(new byte[] { 0x11, (byte)'x' });
    }

    [Test]
    public async Task Lone_prefix_then_normal_text_forwards_prefix_first() {
        var s = new DetachScanner();
        var (f1, _) = s.Process([0x11]);       // held
        await Assert.That(f1).IsEmpty();
        var (f2, d) = s.Process("a"u8);        // not 'd' → forward prefix then 'a'
        await Assert.That(d).IsFalse();
        await Assert.That(f2).IsEquivalentTo(new byte[] { 0x11, (byte)'a' });
    }
}
