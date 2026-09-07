using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The watcher that notices an advertised vendor's CLI binary changing under a running daemon and
/// asks for the advertisement to be refreshed, so a vendor auto-update does not cost the next
/// reviewer launch.
/// </summary>
public class VendorCliWatcherTests {
    static readonly CliBinaryStat Old = new("/versions/2.1.259/claude", 100, 1);
    static readonly CliBinaryStat New = new("/versions/2.1.263/claude", 100, 2);

    sealed class Harness {
        public readonly List<string>                          Refreshes = [];
        public readonly Dictionary<string, CliBinaryStat?>    Stats     = new(StringComparer.Ordinal);
        public readonly VendorCliWatcher                      Watcher;

        public Harness(params (string Vendor, string CliPath)[] watched) {
            Watcher = VendorCliWatcher.ForTest(
                watched,
                refresh: Refreshes.Add,
                stat: path => Stats.GetValueOrDefault(path));
        }
    }

    [Test]
    public async Task An_unchanged_binary_requests_nothing() {
        var h = new Harness(("claude", "/bin/claude"));
        h.Stats["/bin/claude"] = Old;
        h.Watcher.PrimeBaselines();

        h.Watcher.Tick();

        await Assert.That(h.Refreshes).IsEmpty();
    }

    [Test]
    public async Task A_changed_binary_requests_one_refresh_naming_the_vendor() {
        var h = new Harness(("claude", "/bin/claude"));
        h.Stats["/bin/claude"] = Old;
        h.Watcher.PrimeBaselines();

        h.Stats["/bin/claude"] = New;
        h.Watcher.Tick();
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
        await Assert.That(h.Refreshes[0]).Contains("claude");
    }

    // A symlink flip to a new version directory is the common shape of a vendor update, and the
    // new build can have the same size and a newer mtime that a coarse clock still rounds equal.
    [Test]
    public async Task A_symlink_retarget_alone_counts_as_a_change() {
        var h = new Harness(("claude", "/bin/claude"));
        h.Stats["/bin/claude"] = Old;
        h.Watcher.PrimeBaselines();

        h.Stats["/bin/claude"] = Old with { ResolvedPath = "/versions/2.1.263/claude" };
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_transient_stat_failure_neither_refreshes_nor_moves_the_baseline() {
        var h = new Harness(("claude", "/bin/claude"));
        h.Stats["/bin/claude"] = Old;
        h.Watcher.PrimeBaselines();

        h.Stats["/bin/claude"] = null;   // mid-install: the binary is briefly gone
        h.Watcher.Tick();
        h.Stats["/bin/claude"] = Old;    // ...and back, unchanged
        h.Watcher.Tick();

        await Assert.That(h.Refreshes).IsEmpty();
    }

    [Test]
    public async Task A_binary_that_was_missing_at_startup_is_noticed_when_it_appears() {
        var h = new Harness(("claude", "/bin/claude"));
        h.Watcher.PrimeBaselines();      // stat returns null: nothing to fingerprint yet

        h.Stats["/bin/claude"] = New;
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Several_vendors_changing_in_one_tick_share_one_refresh() {
        var h = new Harness(("claude", "/bin/claude"), ("codex", "/bin/codex"));
        h.Stats["/bin/claude"] = Old;
        h.Stats["/bin/codex"]  = new("/lib/codex.js", 500, 1);
        h.Watcher.PrimeBaselines();

        h.Stats["/bin/claude"] = New;
        h.Stats["/bin/codex"]  = new("/lib/codex.js", 520, 2);
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
        await Assert.That(h.Refreshes[0]).Contains("claude");
        await Assert.That(h.Refreshes[0]).Contains("codex");
    }

    [Test]
    public async Task Only_the_changed_vendor_is_named() {
        var h = new Harness(("claude", "/bin/claude"), ("codex", "/bin/codex"));
        h.Stats["/bin/claude"] = Old;
        h.Stats["/bin/codex"]  = new("/lib/codex.js", 500, 1);
        h.Watcher.PrimeBaselines();

        h.Stats["/bin/codex"] = new("/lib/codex.js", 520, 2);
        h.Watcher.Tick();

        await Assert.That(h.Refreshes.Count).IsEqualTo(1);
        await Assert.That(h.Refreshes[0]).Contains("codex");
        await Assert.That(h.Refreshes[0]).DoesNotContain("claude");
    }

    // The fingerprint follows every link to the file that actually runs: a bare command name is
    // resolved on PATH, and a symlink chain is walked to its final target.
    [Test]
    public async Task The_real_fingerprint_follows_symlinks_to_the_installed_file() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Symlink creation needs no privilege only on Unix.");
        using var tmp = new TempDir();
        var target = tmp.CreateFile("versions/2.1.263/claude", "#!/bin/sh\necho 2.1.263\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var link = tmp.PathTo("bin/claude");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);

        var stat = VendorCliWatcher.StatCliBinary(link);

        await Assert.That(stat).IsNotNull();
        await Assert.That(stat!.Value.ResolvedPath).IsEqualTo(new FileInfo(target).FullName);
        await Assert.That(stat.Value.Size).IsEqualTo(new FileInfo(target).Length);
    }

    [Test]
    public async Task A_missing_binary_has_no_fingerprint() {
        using var tmp = new TempDir();

        await Assert.That(VendorCliWatcher.StatCliBinary(tmp.PathTo("nope"))).IsNull();
    }
}
