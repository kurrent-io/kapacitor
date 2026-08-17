using System.Runtime.Versioning;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The affirmed-version record is what makes a kiro-cli upgrade fail closed. Every read path must
/// degrade to "not affirmed" rather than throwing — the daemon reads it at launch, and a boot must
/// not brick on a corrupt file.
/// </summary>
public class KiroReviewerVersionStoreTests {
    // The store is vendor-keyed; these cases exercise it through the Kiro key, whose ON-DISK filename
    // is deliberately unchanged from when this type was Kiro-only — a rename would have silently
    // discarded every existing affirmation and taken shipped reviewers offline on upgrade.
    const string Kiro = "kiro";

    static string TempStateDir() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-kiro-ver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task AnAbsentRecord_ReadsNull() =>
        await Assert.That(new ReviewerVersionStore(TempStateDir(), Kiro).Affirmed).IsNull();

    [Test]
    public async Task AffirmThenRead_RoundTrips() {
        var dir = TempStateDir();
        new ReviewerVersionStore(dir, Kiro).Affirm("2.16.0");

        await Assert.That(new ReviewerVersionStore(dir, Kiro).Affirmed).IsEqualTo("2.16.0");
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task TheRecord_IsOwnerOnly() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX file-mode semantics.");

        var dir = TempStateDir();
        new ReviewerVersionStore(dir, Kiro).Affirm("2.16.0");

        await Assert.That(File.GetUnixFileMode(Path.Combine(dir, ReviewerVersionStore.FileNameFor(Kiro))))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>A directory sitting at the pathname is the shape that would throw from a naive read.</summary>
    [Test]
    public async Task AnUnreadableRecord_ReadsNullRatherThanThrowing() {
        var dir = TempStateDir();
        Directory.CreateDirectory(Path.Combine(dir, ReviewerVersionStore.FileNameFor(Kiro)));

        await Assert.That(new ReviewerVersionStore(dir, Kiro).Affirmed).IsNull();
    }

    /// <summary>A whitespace-only record is not an affirmation — it must not read as one.</summary>
    [Test]
    public async Task AWhitespaceOnlyRecord_ReadsNull() {
        var dir = TempStateDir();
        await File.WriteAllTextAsync(Path.Combine(dir, ReviewerVersionStore.FileNameFor(Kiro)), "   \n");

        await Assert.That(new ReviewerVersionStore(dir, Kiro).Affirmed).IsNull();
    }

    [Test]
    public async Task Affirm_OverwritesRatherThanAppending() {
        var dir = TempStateDir();
        var store = new ReviewerVersionStore(dir, Kiro);
        store.Affirm("2.16.0");
        store.Affirm("2.17.0");

        await Assert.That(store.Affirmed).IsEqualTo("2.17.0");
    }

    /// <summary>
    /// RecordExists must be distinct from "Affirmed is non-null". Boot seeds on absence, so
    /// conflating the two would let a record deleted after an upgrade be silently re-seeded — and
    /// would make boot attempt a write that a directory at the pathname turns into a crash.
    /// </summary>
    [Test]
    public async Task RecordExists_IsTrueForACorruptRecordThatAffirmsNothing() {
        var dir = TempStateDir();
        Directory.CreateDirectory(Path.Combine(dir, ReviewerVersionStore.FileNameFor(Kiro)));

        await Assert.That(ReviewerVersionStore.RecordExists(dir, Kiro)).IsTrue();
        await Assert.That(new ReviewerVersionStore(dir, Kiro).Affirmed).IsNull();
    }

    [Test]
    public async Task RecordExists_IsFalseWhenNothingHasEverBeenWritten() =>
        await Assert.That(ReviewerVersionStore.RecordExists(TempStateDir(), Kiro)).IsFalse();
}
