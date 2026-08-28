using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeImportSourceTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task vendor_is_claude() {
        var src = new ClaudeImportSource(Config.Root, new ClaudePaths(Home, null).Projects);
        await Assert.That(src.Vendor).IsEqualTo("claude");
    }

    [Test]
    public async Task supports_title_generation() {
        var src = new ClaudeImportSource(Config.Root, new ClaudePaths(Home, null).Projects);
        await Assert.That(src.SupportsTitleGeneration).IsTrue();
    }

    [Test]
    public async Task is_available_when_projects_dir_exists() {
        using var tmp = new TempDir();
        var src = new ClaudeImportSource(Config.Root, tmp.Path);
        await Assert.That(src.IsAvailable).IsTrue();
    }

    [Test]
    public async Task is_unavailable_when_projects_dir_missing() {
        var missing = Path.Combine(Path.GetTempPath(), "kcap-claude-source-missing-" + Guid.NewGuid().ToString("N"));
        var src     = new ClaudeImportSource(Config.Root, missing);
        await Assert.That(src.IsAvailable).IsFalse();
    }

    [Test]
    public async Task import_session_async_throws_not_implemented() {
        var src = new ClaudeImportSource(Config.Root, new ClaudePaths(Home, null).Projects);
        var classification = new ImportCommand.SessionClassification {
            SessionId  = "abc",
            FilePath   = "/tmp/none",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
        };
        var ctx = new ImportContext(new HttpClient(), "http://localhost", ForcePrivate: false);

        var ex = await Assert.ThrowsAsync<NotImplementedException>(
            () => src.ImportSessionAsync(classification, ctx, CancellationToken.None)
        );

        await Assert.That(ex?.Message).Contains("ImportChainsAsync");
    }
}
