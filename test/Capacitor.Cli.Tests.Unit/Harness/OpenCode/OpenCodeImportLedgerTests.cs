using Capacitor.Cli.Harness.OpenCode;

namespace Capacitor.Cli.Tests.Unit.Harness.OpenCode;

public class OpenCodeImportLedgerTests {
    [Test]
    public async Task records_and_reports_complete_only_for_matching_fingerprint_and_server() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "ledger.json");
        const string url = "https://srv.example";

        var ledger = OpenCodeImportLedger.Load(path);
        await Assert.That(ledger.IsComplete(url, "ses_x", "fp1")).IsFalse();

        ledger.MarkComplete(url, "ses_x", "fp1");
        ledger.Save();

        var reloaded = OpenCodeImportLedger.Load(path);
        await Assert.That(reloaded.IsComplete(url, "ses_x", "fp1")).IsTrue();
        await Assert.That(reloaded.IsComplete(url, "ses_x", "fp2")).IsFalse(); // content changed → re-import
        await Assert.That(reloaded.IsComplete("https://other", "ses_x", "fp1")).IsFalse();
        await Assert.That(reloaded.IsComplete(url, "ses_y", "fp1")).IsFalse();
    }

    [Test]
    public async Task load_tolerates_missing_or_corrupt_file() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "nope.json");
        await Assert.That(OpenCodeImportLedger.Load(path).IsComplete("u", "s", "fp")).IsFalse();
        await File.WriteAllTextAsync(path, "{ not json");
        await Assert.That(OpenCodeImportLedger.Load(path).IsComplete("u", "s", "fp")).IsFalse();
    }
}
