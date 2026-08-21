using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Capacitor.Cli.Services;

static class ServiceFiles {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Writes a service unit readable only by its owner, or fails without leaving one behind.
    ///
    /// <para><see cref="File.WriteAllText(string,string)"/> defaults to <c>-rw-r--r--</c> (verified on a
    /// real launchd install), and a unit carries the server URL, the profile, and possibly a command that
    /// produces a credential.</para>
    ///
    /// <para>Order matters, and each step closes a specific hole: the staging inode is created
    /// EXCLUSIVELY with its mode requested at creation; the mode is then verified and repaired
    /// <b>through the open handle, before any content is written</b>, so no populated file ever exists at
    /// a permissive mode and there is no pathname to re-resolve; only then is content written and the file
    /// renamed into place. The post-rename check exists because a rename onto an existing target does not
    /// preserve the source mode on every filesystem, and if it fails the live file is REMOVED — reporting
    /// a failed install while leaving a readable credential-bearing unit where launchd would read it is
    /// the failure this is meant to prevent.</para></summary>
    /// <param name="verifyFinal">Test seam. Production passes null and gets the real post-rename check;
    /// a test supplies a failing one to prove the rollback, which is otherwise only reachable on a
    /// filesystem that does not preserve mode across a rename.</param>
    public static void WriteOwnerOnly(
            string path, string content, Encoding? encoding = null, Action<string>? verifyFinal = null) {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
            RequireNotWorldWritable(directory);
        }

        // Full GUID: the staging name must not be guessable by a local process racing to pre-create it,
        // and CreateNew turns such a collision into a hard failure rather than a followed symlink.
        var staging = path + ".tmp-" + Guid.NewGuid().ToString("N");

        try {
            WriteStaging(staging, content, encoding);
            File.Move(staging, path, overwrite: true);
        } catch (Exception) {
            TryDelete(staging);

            throw;
        }

        try {
            (verifyFinal ?? RequireOwnerOnlyPath)(path);
        } catch (Exception) {
            TryDelete(path);   // never leave an insecure unit at the live path

            throw;
        }
    }

    static void WriteStaging(string staging, string content, Encoding? encoding) {
        var options = new FileStreamOptions {
            Mode   = FileMode.CreateNew,       // never follow or truncate an existing entry
            Access = FileAccess.Write
        };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;

        using var stream = new FileStream(staging, options);

        // Before content: UnixCreateMode is still filtered through the process umask, so the requested
        // mode is a request, not a result. Checked on the handle so it cannot be a different file.
        RequireOwnerOnlyHandle(stream.SafeFileHandle, staging);

        using var writer = encoding is null ? new StreamWriter(stream) : new StreamWriter(stream, encoding);

        writer.Write(content);
    }

    /// <summary>Refuses to write a unit into a directory other local accounts can write — owner-only mode
    /// on the unit is no protection when someone else can replace the unit and choose what the daemon
    /// runs.</summary>
    static void RequireNotWorldWritable(string directory) {
        if (OperatingSystem.IsWindows()) return;   // ACL-governed, inherited from the user profile

        var mode = File.GetUnixFileMode(directory);

        if (mode.HasFlag(UnixFileMode.OtherWrite) || mode.HasFlag(UnixFileMode.GroupWrite))
            throw new InvalidOperationException(
                $"Refusing to write a service unit into a group- or world-writable directory: {directory}. "
              + "Another local account could replace the unit and choose what the daemon runs.");
    }

    /// <summary>Requires EXACTLY owner read+write on the open handle, repairing once.
    ///
    /// <para>Exactly, not "nothing extra": a restrictive umask can strip the owner bits too, and a
    /// <c>0000</c> unit is one launchd cannot read — an install that reported success while producing a
    /// service that never starts, and a mode the docs promise is <c>0600</c>.</para></summary>
    static void RequireOwnerOnlyHandle(SafeFileHandle handle, string path) {
        if (OperatingSystem.IsWindows()) return;

        if (File.GetUnixFileMode(handle) == OwnerOnly) return;

        File.SetUnixFileMode(handle, OwnerOnly);   // umask does not apply to an explicit chmod

        var mode = File.GetUnixFileMode(handle);

        if (mode != OwnerOnly)
            throw new InvalidOperationException(
                $"Could not establish owner-only permissions on {path} (mode is {mode}). A service unit may "
              + "carry a token-producing command, so installation fails rather than continuing.");
    }

    static void RequireOwnerOnlyPath(string path) {
        if (OperatingSystem.IsWindows()) return;

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        RequireOwnerOnlyHandle(handle, path);
    }

    static void TryDelete(string path) {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* best-effort */ }
    }
}
