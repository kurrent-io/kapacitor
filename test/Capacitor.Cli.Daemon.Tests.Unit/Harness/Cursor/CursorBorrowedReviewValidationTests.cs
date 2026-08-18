using Capacitor.Cli.Daemon.Harness.Cursor;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Cursor;

public class CursorBorrowedReviewValidationTests {
    [Test]
    public async Task BundleDigest_IgnoresTransientRunningDirectory() {
        using var tmp = new TempDir();

        tmp.CreateFile("cursor-agent", "artifact");
        var before = CursorBorrowedReviewValidation.ComputeBundleDigest(tmp.Path);
        var running = tmp.CreateDir(".running");
        File.WriteAllText(Path.Combine(running, "12345"), "");

        var after = CursorBorrowedReviewValidation.ComputeBundleDigest(tmp.Path);

        await Assert.That(after).IsEqualTo(before);
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
