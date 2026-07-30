using System.Text;

namespace Capacitor.Cli.Services;

static class ServiceFiles {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Writes a service unit whose content is never readable by anyone but its owner.
    ///
    /// <para><see cref="File.WriteAllText(string,string)"/> defaults to <c>-rw-r--r--</c> (verified on a
    /// real launchd install), and a unit carries the server URL, the profile, and possibly a command that
    /// produces a credential.</para>
    ///
    /// <para>The staging inode is created EXCLUSIVELY with its mode set at creation
    /// (<see cref="FileStreamOptions.UnixCreateMode"/>) and written through that same handle. Create,
    /// then chmod by pathname, then reopen by pathname would leave two windows: a pre-created staging
    /// entry or symlink would be followed, and the content would land before the mode did. Failure to
    /// establish owner-only mode THROWS rather than falling back to a readable file.</para></summary>
    public static void WriteOwnerOnly(string path, string content, Encoding? encoding = null) {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
            RequireNotWorldWritable(directory);
        }

        // Full GUID, not a truncated one: the staging name must not be guessable by a local process
        // racing to pre-create it. CreateNew makes that a hard failure rather than a followed symlink.
        var staging = path + ".tmp-" + Guid.NewGuid().ToString("N");

        try {
            WriteStaging(staging, content, encoding);
            File.Move(staging, path, overwrite: true);
        } catch (Exception) {
            TryDelete(staging);

            throw;
        }

        // The mode travels with the inode through a rename, but an overwritten target on some
        // filesystems can retain the previous file's permissions — so verify the real outcome.
        RequireOwnerOnly(path);
    }

    static void WriteStaging(string staging, string content, Encoding? encoding) {
        var options = new FileStreamOptions {
            Mode   = FileMode.CreateNew,       // never follow or truncate an existing entry
            Access = FileAccess.Write
        };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;

        using var stream = new FileStream(staging, options);
        using var writer = encoding is null ? new StreamWriter(stream) : new StreamWriter(stream, encoding);

        writer.Write(content);
    }

    /// <summary>Refuses to write a unit into a directory other local accounts can replace files in —
    /// owner-only mode on the unit is no protection when the containing directory is writable.</summary>
    static void RequireNotWorldWritable(string directory) {
        if (OperatingSystem.IsWindows()) return;   // ACL-governed; inherited from the user profile

        var mode = File.GetUnixFileMode(directory);

        if (mode.HasFlag(UnixFileMode.OtherWrite) || mode.HasFlag(UnixFileMode.GroupWrite))
            throw new InvalidOperationException(
                $"Refusing to write a service unit into a group- or world-writable directory: {directory}. "
              + "Another local account could replace the unit and choose what the daemon runs.");
    }

    /// <summary>Verifies the final mode, throwing rather than leaving a readable unit in place.</summary>
    static void RequireOwnerOnly(string path) {
        if (OperatingSystem.IsWindows()) return;

        var mode = File.GetUnixFileMode(path);

        if ((mode & ~OwnerOnly) != 0) {
            RestrictToOwner(path);

            if ((File.GetUnixFileMode(path) & ~OwnerOnly) != 0)
                throw new InvalidOperationException(
                    $"Could not restrict {path} to its owner (mode is {mode}). A service unit may carry a "
                  + "token-producing command, so installation fails rather than leaving it readable.");
        }
    }

    /// <summary>Best-effort chmod, used only as the recovery step inside
    /// <see cref="RequireOwnerOnly"/> — which then re-checks and throws if it did not take.</summary>
    static void RestrictToOwner(string path) {
        try {
            File.SetUnixFileMode(path, OwnerOnly);
        } catch (Exception) {
            // RequireOwnerOnly re-reads the mode and raises the real error.
        }
    }

    static void TryDelete(string path) {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* best-effort */ }
    }
}
