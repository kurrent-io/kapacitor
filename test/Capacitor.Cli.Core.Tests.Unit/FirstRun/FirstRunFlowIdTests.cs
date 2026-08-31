using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

// The server can check length and alphabet but not entropy, so the 128-bit guarantee is this
// generator's alone and these are what hold it to it.
public class FirstRunFlowIdTests {
    [Test]
    public async Task Is_22_characters__the_servers_floor_for_128_bits() {
        // 22 is base64url of 16 bytes, and the server refuses anything shorter precisely so that a
        // weaker id cannot fit. Generating a longer one would pass; generating a shorter one is the
        // regression this pins.
        await Assert.That(FirstRunFlowId.New()).Length().IsEqualTo(22);
    }

    [Test]
    public async Task Is_base64url__so_it_carries_no_path_or_query_meaning() {
        // The id reaches a URL, a stream name and a log line. The server refuses anything outside
        // this alphabet, so a padded or standard-base64 generator here would produce ids no server
        // accepts — a failure that would only show against a real one.
        foreach (var c in FirstRunFlowId.New())
            await Assert.That(char.IsAsciiLetterOrDigit(c) || c is '-' or '_').IsTrue();
    }

    [Test]
    public async Task Does_not_repeat() {
        var ids = Enumerable.Range(0, 256).Select(_ => FirstRunFlowId.New()).ToHashSet(StringComparer.Ordinal);

        await Assert.That(ids).Count().IsEqualTo(256);
    }
}
