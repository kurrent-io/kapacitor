using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Cursor;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Cursor;

public class CursorBorrowedReviewValidationTests {
    /// <summary>Every path here is rooted, which resolves without a search path.</summary>
    static CliResolver Cli => new(BinaryProbe.Searching(null));

    [Test]
    public async Task BundleDigest_IgnoresTransientRunningDirectory() {
        using var tmp = new TempDir();

        tmp.CreateFile("cursor-agent", "artifact");
        var before = CursorBorrowedReviewValidation.ComputeBundleDigest(tmp.Path);
        var running = tmp.CreateDir(".running");
        running.CreateFile("12345", "");

        var after = CursorBorrowedReviewValidation.ComputeBundleDigest(tmp.Path);

        await Assert.That(after).IsEqualTo(before);
    }

    /// <summary>The marker is informational: a build that does not match it must still return
    /// <see langword="null"/> WITHOUT throwing, because every production path now treats a non-match
    /// as the ordinary steady state rather than an error.</summary>
    [Test]
    public async Task TryMatchValidatedBuild_NonMatchingPath_ReturnsNullWithoutThrowing() {
        using var pathDir = TempDir.WithPathTo("kcap-not-cursor", out var path);

        await Assert.That(CursorBorrowedReviewValidation.TryMatchValidatedBuild(Cli, path)).IsNull();
    }
}
