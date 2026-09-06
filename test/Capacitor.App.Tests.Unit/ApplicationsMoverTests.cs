using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// The fake runner stands in for `ditto`: it materialises whatever the test says a copy produced,
/// at the staging path the mover chose. Promotion is injected so the rename semantics stay a
/// macOS-only test below; here it is a plain Directory.Move.
public class ApplicationsMoverTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class FakeDitto(Action<string> populate) : IProcessRunner {
        public int Calls;

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls++;
            populate(args[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    static void CompleteBundle(string root) {
        Directory.CreateDirectory(Path.Combine(root, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(root, "Contents", "Info.plist"), "<plist/>");
        File.WriteAllText(Path.Combine(root, "Contents", "MacOS", "Kurrent Capacitor"), "exe");
    }

    static bool MovePromote(string from, string to) {
        if (Directory.Exists(to)) return false;
        Directory.Move(from, to);
        return true;
    }

    [Test]
    public async Task Complete_copy_is_promoted_and_nothing_is_left_staged() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
        var mover = new ApplicationsMover(new FakeDitto(CompleteBundle), MovePromote, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(outcome.InstalledPath).IsEqualTo(Path.Combine(apps, "Kurrent Capacitor.app"));
        await Assert.That(Directory.GetDirectories(apps).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Incomplete_copy_is_removed_and_reported() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
        var mover = new ApplicationsMover(new FakeDitto(root => Directory.CreateDirectory(Path.Combine(root, "Contents"))), MovePromote, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(outcome.Error).Contains("incomplete");
        await Assert.That(Directory.GetDirectories(apps)).IsEmpty();
    }

    [Test]
    public async Task Existing_destination_refuses_before_copying() {
        var apps = Tmp.CreateDir("Applications");
        Tmp.CreateDir("Applications/Kurrent Capacitor.app");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
        var ditto = new FakeDitto(CompleteBundle);
        var mover = new ApplicationsMover(ditto, MovePromote, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(outcome.Error).Contains("already exists");
        await Assert.That(ditto.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Destination_appearing_mid_move_fails_promotion_and_cleans_staging() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
        var mover = new ApplicationsMover(new FakeDitto(CompleteBundle), (_, _) => false, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(outcome.Error).Contains("appeared");
        await Assert.That(Directory.GetDirectories(apps)).IsEmpty();
    }

    /// renamex_np is macOS-only; elsewhere this test is a no-op. An EMPTY existing destination is
    /// the case a plain rename would silently replace.
    [Test]
    public async Task PromoteExclusive_refuses_an_empty_existing_destination() {
        if (!OperatingSystem.IsMacOS()) return;
        var from = Tmp.CreateDir("staging");
        var to = Tmp.CreateDir("target");

        await Assert.That(ApplicationsMover.PromoteExclusive(from, to)).IsFalse();
        await Assert.That(Directory.Exists(from)).IsTrue();
    }

    [Test]
    public async Task PromoteExclusive_moves_when_the_destination_is_absent() {
        if (!OperatingSystem.IsMacOS()) return;
        var from = Tmp.CreateDir("staging");
        var to = Tmp.PathTo("target");

        await Assert.That(ApplicationsMover.PromoteExclusive(from, to)).IsTrue();
        await Assert.That(Directory.Exists(to)).IsTrue();
    }
}
