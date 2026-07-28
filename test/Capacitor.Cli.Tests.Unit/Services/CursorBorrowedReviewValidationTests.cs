using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class CursorBorrowedReviewValidationTests {
    [Test]
    public async Task BundleDigest_IgnoresTransientRunningDirectory() {
        var root = Directory.CreateTempSubdirectory("cursor-validation-");
        try {
            File.WriteAllText(Path.Combine(root.FullName, "cursor-agent"), "artifact");
            var before = CursorBorrowedReviewValidation.ComputeBundleDigest(root.FullName);
            var running = Directory.CreateDirectory(Path.Combine(root.FullName, ".running"));
            File.WriteAllText(Path.Combine(running.FullName, "12345"), "");

            var after = CursorBorrowedReviewValidation.ComputeBundleDigest(root.FullName);

            await Assert.That(after).IsEqualTo(before);
        } finally {
            try { root.Delete(recursive: true); } catch { }
        }
    }

    /// <summary>The marker is informational: a build that does not match it must still return
    /// <see langword="null"/> WITHOUT throwing, because every production path now treats a non-match
    /// as the ordinary steady state rather than an error.</summary>
    [Test]
    public async Task TryMatchValidatedBuild_NonMatchingPath_ReturnsNullWithoutThrowing() {
        var path = Path.Combine(Path.GetTempPath(), "kcap-not-cursor-" + Guid.NewGuid().ToString("N")[..8]);

        await Assert.That(CursorBorrowedReviewValidation.TryMatchValidatedBuild(path)).IsNull();
    }
}
