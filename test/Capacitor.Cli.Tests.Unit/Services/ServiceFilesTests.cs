using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Service unit files must not be world-readable. They already carried the server URL and profile, and
/// now carry a command that produces a credential; <see cref="File.WriteAllText(string,string)"/>
/// defaults to <c>-rw-r--r--</c>, which was verified on a real launchd install.
/// </summary>
public class ServiceFilesTests {
    [Test]
    public async Task RestrictToOwner_removes_group_and_other_access() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes; Windows inherits the directory ACL");

        var path = Path.Combine(Path.GetTempPath(), $"kcap-unit-{Guid.NewGuid():N}.plist");
        try {
            // Written exactly the way the service managers write it, so the starting mode is the real
            // default rather than something this test chose.
            await File.WriteAllTextAsync(path, "<plist/>");

            ServiceFiles.RestrictToOwner(path);
            var mode = File.GetUnixFileMode(path);

            await Assert.That(mode.HasFlag(UnixFileMode.OtherRead)).IsFalse();
            await Assert.That(mode.HasFlag(UnixFileMode.GroupRead)).IsFalse();
            await Assert.That(mode.HasFlag(UnixFileMode.OtherWrite)).IsFalse();
            await Assert.That(mode.HasFlag(UnixFileMode.GroupWrite)).IsFalse();
            // Still usable by its owner — launchd/systemd read it as the same user.
            await Assert.That(mode.HasFlag(UnixFileMode.UserRead)).IsTrue();
            await Assert.That(mode.HasFlag(UnixFileMode.UserWrite)).IsTrue();
        } finally {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    /// <summary>The default really is world-readable, so the assertion above is not vacuous. Without
    /// this, a platform that happened to write 0600 already would make the test pass while
    /// <see cref="ServiceFiles.RestrictToOwner"/> did nothing.</summary>
    [Test]
    public async Task The_default_write_mode_is_world_readable_so_the_fix_is_load_bearing() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        var path = Path.Combine(Path.GetTempPath(), $"kcap-unit-default-{Guid.NewGuid():N}.plist");
        try {
            await File.WriteAllTextAsync(path, "<plist/>");

            await Assert.That(File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherRead)).IsTrue()
                .Because("if this ever stops being true, RestrictToOwner is no longer the thing protecting the unit");
        } finally {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    /// <summary>A path that cannot be chmod'ed must not fail an otherwise valid install.</summary>
    [Test]
    public async Task RestrictToOwner_on_a_missing_path_does_not_throw() {
        ServiceFiles.RestrictToOwner(Path.Combine(Path.GetTempPath(), $"kcap-absent-{Guid.NewGuid():N}"));

        await Assert.That(true).IsTrue();
    }
}
