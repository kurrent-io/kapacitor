using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// A service unit carries the server URL, the profile, and possibly a command that produces a
/// credential. <see cref="File.WriteAllText(string,string)"/> defaults to <c>-rw-r--r--</c> — verified on
/// a real launchd install — so the write path has to establish owner-only mode itself, and prove it.
/// </summary>
public partial class ServiceFilesTests {
    [LibraryImport("libc", EntryPoint = "umask")]
    private static partial uint umask(uint mask);

    static string TempDir(string tag) {
        var dir = Path.Combine(Path.GetTempPath(), $"kcap-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        return dir;
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_writes_the_content_and_leaves_it_owner_only() {
        var dir  = TempDir("write");
        var path = Path.Combine(dir, "unit.plist");
        try {
            ServiceFiles.WriteOwnerOnly(path, "<plist>KCAP_COPILOT_TOKEN_CMD</plist>");

            await Assert.That(await File.ReadAllTextAsync(path))
                .IsEqualTo("<plist>KCAP_COPILOT_TOKEN_CMD</plist>");

            Skip.When(OperatingSystem.IsWindows(), "POSIX modes; Windows inherits the directory ACL");
            await Assert.That(File.GetUnixFileMode(path))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        } finally {
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>The default write mode really is world-readable, so the assertion above is not vacuous.
    /// Without this, a platform that already wrote 0600 would make the whole file pass while the write
    /// path did nothing.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task The_default_write_mode_is_world_readable_so_the_fix_is_load_bearing() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        var dir  = TempDir("default");
        var path = Path.Combine(dir, "unit.plist");
        try {
            await File.WriteAllTextAsync(path, "<plist/>");

            await Assert.That(File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherRead)).IsTrue()
                .Because("if this stops being true, WriteOwnerOnly is no longer what protects the unit");
        } finally {
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Overwriting an existing world-readable unit ends up owner-only rather than inheriting the
    /// old mode, and no staging file is left beside it.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_overwrites_a_world_readable_unit_and_leaves_no_staging_file() {
        var dir  = TempDir("overwrite");
        var path = Path.Combine(dir, "unit.plist");
        try {
            await File.WriteAllTextAsync(path, "old");
            ServiceFiles.WriteOwnerOnly(path, "new");

            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("new");
            await Assert.That(Directory.GetFiles(dir).Length).IsEqualTo(1)
                .Because("the staging file must be moved, not left beside the unit");

            Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");
            await Assert.That(File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherRead)).IsFalse();
        } finally {
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>The mode is EXACTLY owner read+write under either a permissive or a restrictive umask.
    ///
    /// <para>Both directions matter and a group/other-bits-only assertion catches neither. A permissive
    /// umask (0000) is the leak case. A restrictive one (0777) is the opposite failure:
    /// <c>UnixCreateMode</c> is filtered through the umask, so the file can land <c>0000</c> — which no
    /// "nothing extra than owner-only" check rejects, and which launchd cannot read, so the install would
    /// report success and produce a service that never starts.</para>
    ///
    /// <para>Serialized because the umask is process-global.</para></summary>
    [Test]
    [NotInParallel]
    [Arguments(0u)]
    [Arguments(0x3Fu)]    // umask 077
    [Arguments(0x1FFu)]   // umask 777
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_produces_exactly_owner_read_write_under_any_umask(uint mask) {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        var dir      = TempDir("umask");
        var path     = Path.Combine(dir, "unit.plist");
        var previous = umask(mask);
        try {
            ServiceFiles.WriteOwnerOnly(path, "SECRET-COMMAND");

            await Assert.That(File.GetUnixFileMode(path))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // The property that actually matters to launchd: the owner can still read it.
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("SECRET-COMMAND");
        } finally {
            _ = umask(previous);
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>If the final mode cannot be guaranteed, no unit is left at the live path.
    ///
    /// <para>The failure is injected, because on a normal filesystem a rename preserves the mode and this
    /// branch is unreachable. It is worth proving anyway: an earlier revision ran the post-rename check
    /// outside the cleanup scope, so a failure there threw while leaving a readable credential-bearing unit
    /// exactly where launchd would consume it — a failed install that still published the secret.</para></summary>
    [Test]
    public async Task WriteOwnerOnly_removes_the_live_unit_when_the_final_check_fails() {
        var dir  = TempDir("rollback");
        var path = Path.Combine(dir, "unit.plist");
        try {
            var ex = Assert.Throws<InvalidOperationException>(() => ServiceFiles.WriteOwnerOnly(
                path, "SECRET-COMMAND", null,
                verifyFinal: _ => throw new InvalidOperationException("mode could not be guaranteed")));

            await Assert.That(ex!.Message).Contains("guaranteed");
            await Assert.That(File.Exists(path)).IsFalse()
                .Because("a failed install must not leave a unit at the path launchd reads");
            await Assert.That(Directory.GetFiles(dir)).IsEmpty()
                .Because("the staging file must not survive either");
        } finally {
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Installing into a directory other local accounts can write is refused: owner-only mode on
    /// the unit is no protection if someone else can replace the unit and choose what the daemon runs.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_refuses_a_world_writable_directory() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        var dir = TempDir("worldwritable");
        try {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

            var ex = Assert.Throws<InvalidOperationException>(
                () => ServiceFiles.WriteOwnerOnly(Path.Combine(dir, "unit.plist"), "x"));

            await Assert.That(ex!.Message).Contains("writable");
            await Assert.That(File.Exists(Path.Combine(dir, "unit.plist"))).IsFalse();
        } finally {
            try {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(dir, true);
            } catch { /* best-effort */ }
        }
    }

    /// <summary>A pre-existing entry at the staging path is not followed or truncated — the staging inode
    /// is created exclusively. The name carries a full GUID, so this asserts the mechanism rather than a
    /// realistic collision.</summary>
    [Test]
    public async Task WriteOwnerOnly_leaves_an_unrelated_file_in_the_directory_alone() {
        var dir       = TempDir("staging");
        var path      = Path.Combine(dir, "unit.plist");
        var bystander = Path.Combine(dir, "unit.plist.tmp-not-ours");
        try {
            await File.WriteAllTextAsync(bystander, "PRE-EXISTING");
            ServiceFiles.WriteOwnerOnly(path, "new");

            await Assert.That(await File.ReadAllTextAsync(bystander)).IsEqualTo("PRE-EXISTING");
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("new");
        } finally {
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    // ── manager wiring ───────────────────────────────────────────────────────────────────────────
    //
    // Each manager's Install invokes launchctl/systemctl/schtasks, so the write half is split out and
    // driven with an injected writer. Without this, all of the above could hold while a manager still
    // called File.WriteAllText directly.

    [Test]
    public async Task Launchd_writes_its_plist_through_the_secure_writer() {
        var seen = new List<string>();
        var mgr  = new LaunchdServiceManager((path, content, _) => seen.Add(path + "|" + content));

        mgr.WriteUnitFiles(Spec());

        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen[0]).Contains(".plist");
        await Assert.That(seen[0]).Contains("KCAP_COPILOT_TOKEN_CMD");
    }

    [Test]
    public async Task Systemd_writes_its_unit_through_the_secure_writer() {
        var seen = new List<string>();
        var mgr  = new SystemdServiceManager((path, content, _) => seen.Add(path + "|" + content));

        mgr.WriteUnitFiles(Spec());

        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen[0]).Contains(".service");
        await Assert.That(seen[0]).Contains("KCAP_COPILOT_TOKEN_CMD");
    }

    /// <summary>Windows writes two files and both go through the writer — the task XML as UTF-16, the
    /// wrapper as UTF-8. (The token command itself is excluded from the captured environment on Windows;
    /// this asserts the write wiring, not that variable.)</summary>
    [Test]
    public async Task Windows_writes_both_units_through_the_secure_writer() {
        var seen = new List<(string Path, Encoding? Encoding)>();
        var mgr  = new WindowsScheduledTaskServiceManager((path, _, encoding) => seen.Add((path, encoding)));

        mgr.WriteUnitFiles(Spec());

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen.Any(f => f.Path.EndsWith(".task.xml", StringComparison.Ordinal) && Equals(f.Encoding, Encoding.Unicode))).IsTrue();
        await Assert.That(seen.Any(f => f.Path.EndsWith(".cmd", StringComparison.Ordinal) && Equals(f.Encoding, Encoding.UTF8))).IsTrue();
    }

    static ServiceSpec Spec() => new(
        ServiceId:        "test",
        DaemonBinaryPath: "/opt/kcap/kcap-daemon",
        LogPath:          "/tmp/kcap-test.log",
        Environment:      new Dictionary<string, string> { ["KCAP_COPILOT_TOKEN_CMD"] = "gh auth token" },
        ExtraArgs:        []);
}
