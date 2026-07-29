namespace Capacitor.Cli.Services;

static class ServiceFiles {
    /// <summary>Makes a written service unit readable and writable by its owner only.
    ///
    /// <para>These files are created with <see cref="File.WriteAllText(string,string)"/>, whose default
    /// mode is world-readable — verified <c>-rw-r--r--</c> on a real launchd install. A unit already
    /// carries the server URL and profile, and now a command that produces a credential, so nothing in
    /// it benefits from being readable by other local accounts.</para>
    ///
    /// <para>Best-effort by design: a filesystem that cannot express the mode (a Windows volume, an
    /// exotic mount) must not fail an otherwise valid install, and the unit is written inside the
    /// user's own profile directory either way. On Windows the ACL is inherited from that directory
    /// rather than set here.</para></summary>
    public static void RestrictToOwner(string path) {
        if (OperatingSystem.IsWindows()) return;

        try {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        } catch (Exception) {
            // Not fatal: see above.
        }
    }
}
