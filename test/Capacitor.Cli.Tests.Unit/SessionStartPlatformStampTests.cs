using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class SessionStartPlatformStampTests {
    [Test]
    public async Task Stamp_carries_the_normalized_platform() {
        // The field feeds the server's live applicability gate; the vocabulary is closed
        // (macos/linux/windows) and every CI host is one of them.
        var body = new JsonObject();
        using var tmp = new TempDir();
        SessionStartInventory.Stamp(body, new ConfigRoot(tmp.Path));

        var platform = (string?)body["platform"];
        await Assert.That(platform).IsEqualTo(HostPlatform.Normalized);
        await Assert.That(new[] { "macos", "linux", "windows" }).Contains(platform!);
    }
}
