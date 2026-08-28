using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

public class CodexImportSourceTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task vendor_is_codex() {
        var src = new CodexImportSource(Config.Root, CodexPaths.FromEnvironment(Home).Sessions);
        await Assert.That(src.Vendor).IsEqualTo("codex");
    }

    [Test]
    public async Task supports_title_generation() {
        var src = new CodexImportSource(Config.Root, CodexPaths.FromEnvironment(Home).Sessions);
        await Assert.That(src.SupportsTitleGeneration).IsTrue();
    }

    [Test]
    public async Task is_available_when_sessions_dir_exists() {
        using var tmp = new TempDir();
        var src = new CodexImportSource(Config.Root, tmp.Path);
        await Assert.That(src.IsAvailable).IsTrue();
    }

    [Test]
    public async Task is_unavailable_when_sessions_dir_missing() {
        var missing = Path.Combine(Path.GetTempPath(), "kcap-codex-source-missing-" + Guid.NewGuid().ToString("N"));
        var src     = new CodexImportSource(Config.Root, missing);
        await Assert.That(src.IsAvailable).IsFalse();
    }

    [Test]
    public async Task import_session_async_throws_not_implemented() {
        var src = new CodexImportSource(Config.Root, CodexPaths.FromEnvironment(Home).Sessions);
        var classification = new ImportCommand.SessionClassification {
            SessionId  = "abc",
            FilePath   = "/tmp/none",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = "codex",
        };
        var ctx = new ImportContext(new HttpClient(), "http://localhost", ForcePrivate: false);

        var ex = await Assert.ThrowsAsync<NotImplementedException>(
            () => src.ImportSessionAsync(classification, ctx, CancellationToken.None)
        );

        await Assert.That(ex?.Message).Contains("ImportChainsAsync");
    }
}
