namespace Capacitor.Cli.Tests.Unit;

/// <summary>Covers the runtime accessor only (<see cref="DaemonDigest"/>) — the build-time
/// generation of <c>GeneratedDigest.Value</c> is exercised by local build
/// verification (placeholder vs. -p:KcapDaemonDigest), not by this unit suite.</summary>
public class DaemonDigestTests {
    [Test]
    public async Task Placeholder_is_not_usable_and_never_matches() {
        // Local dev/test builds carry the placeholder unless -p:KcapDaemonDigest was passed:
        if (!DaemonDigest.IsUsable) {
            using var tmp = new TempDir();
            var       f   = tmp.CreateFile("digest.tmp", "anything");
            await Assert.That(DaemonDigest.Matches(f)).IsFalse();
        }
    }

    [Test]
    public async Task Matches_hashes_file_content() {
        // exercised via the internal seam: compute what Matches computes
        using var tmp = TempDir.WithPathTo("digest.tmp", out var f);
        await File.WriteAllBytesAsync(f, [1, 2, 3]);
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
        await Assert.That(DaemonDigest.HashOf(f)).IsEqualTo(expected);
    }

    [Test]
    public async Task Matches_returns_false_for_a_nonexistent_file_even_when_usable() {
        if (!DaemonDigest.IsUsable) return; // only meaningful when a real digest is embedded
        using var tmp = TempDir.WithPathTo("digest", out var missing);

        await Assert.That(DaemonDigest.Matches(missing)).IsFalse();
    }
}
