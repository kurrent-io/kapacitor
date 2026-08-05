using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The affirmed-version record is what makes a kiro-cli upgrade fail closed. Every read path must
/// degrade to "not affirmed" rather than throwing — the daemon reads it at launch, and a boot must
/// not brick on a corrupt file.
/// </summary>
public class KiroReviewerVersionStoreTests {
    static string TempStateDir() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-kiro-ver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task AnAbsentRecord_ReadsNull() =>
        await Assert.That(new KiroReviewerVersionStore(TempStateDir()).Affirmed).IsNull();

    [Test]
    public async Task AffirmThenRead_RoundTrips() {
        var dir = TempStateDir();
        new KiroReviewerVersionStore(dir).Affirm("2.16.0");

        await Assert.That(new KiroReviewerVersionStore(dir).Affirmed).IsEqualTo("2.16.0");
    }

    [Test]
    public async Task TheRecord_IsOwnerOnly() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX file-mode semantics.");

        var dir = TempStateDir();
        new KiroReviewerVersionStore(dir).Affirm("2.16.0");

        await Assert.That(File.GetUnixFileMode(Path.Combine(dir, KiroReviewerVersionStore.FileName)))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>A directory sitting at the pathname is the shape that would throw from a naive read.</summary>
    [Test]
    public async Task AnUnreadableRecord_ReadsNullRatherThanThrowing() {
        var dir = TempStateDir();
        Directory.CreateDirectory(Path.Combine(dir, KiroReviewerVersionStore.FileName));

        await Assert.That(new KiroReviewerVersionStore(dir).Affirmed).IsNull();
    }

    /// <summary>A whitespace-only record is not an affirmation — it must not read as one.</summary>
    [Test]
    public async Task AWhitespaceOnlyRecord_ReadsNull() {
        var dir = TempStateDir();
        await File.WriteAllTextAsync(Path.Combine(dir, KiroReviewerVersionStore.FileName), "   \n");

        await Assert.That(new KiroReviewerVersionStore(dir).Affirmed).IsNull();
    }

    [Test]
    public async Task Affirm_OverwritesRatherThanAppending() {
        var dir = TempStateDir();
        var store = new KiroReviewerVersionStore(dir);
        store.Affirm("2.16.0");
        store.Affirm("2.17.0");

        await Assert.That(store.Affirmed).IsEqualTo("2.17.0");
    }
}
