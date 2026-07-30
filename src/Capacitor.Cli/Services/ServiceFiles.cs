using System.Text;

namespace Capacitor.Cli.Services;

static class ServiceFiles {
    /// <summary>Writes a service unit whose CONTENT is never world-readable.
    ///
    /// <para><see cref="File.WriteAllText(string,string)"/> defaults to <c>-rw-r--r--</c> (verified on a
    /// real launchd install), and write-then-chmod leaves a window where finished content sits at 644.
    /// So the content goes to a staging file restricted BEFORE it is filled, then moved over the target:
    /// only an empty file is ever world-readable, and the same-directory move is atomic, so no reader
    /// sees a half-written unit.</para></summary>
    public static void WriteOwnerOnly(string path, string content, Encoding? encoding = null) {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var staging = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try {
            // Create empty, restrict, THEN fill: the credential-bearing bytes never exist at 644.
            using (File.Create(staging)) { }

            RestrictToOwner(staging);

            if (encoding is null) File.WriteAllText(staging, content);
            else                  File.WriteAllText(staging, content, encoding);

            File.Move(staging, path, overwrite: true);
        } catch (Exception) {
            try { if (File.Exists(staging)) File.Delete(staging); } catch (Exception) { /* best-effort */ }

            throw;
        }

        // The mode travels with the inode through the move; reasserted because an overwritten target
        // on some filesystems can retain the previous file's permissions.
        RestrictToOwner(path);
    }

    /// <summary>Makes a file readable and writable by its owner only.
    ///
    /// <para>Best-effort by design: a filesystem that cannot express the mode must not fail an otherwise
    /// valid install, and the file is written inside the user's own profile directory either way. On
    /// Windows the ACL is inherited from that directory rather than set here.</para></summary>
    public static void RestrictToOwner(string path) {
        if (OperatingSystem.IsWindows()) return;

        try {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        } catch (Exception) {
            // Not fatal: see above.
        }
    }
}
