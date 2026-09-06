using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

public sealed record MoveOutcome(bool Moved, string? InstalledPath, string? Error);

/// Copies the bundle into a staging sibling on the same volume, verifies the copy, then promotes
/// it with a no-replace rename — a partial copy can never sit at the final path.
public sealed partial class ApplicationsMover(IProcessRunner runner, Func<string, string, bool> promote, string applicationsDir = "/Applications") {
    static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(2);

    public async Task<MoveOutcome> MoveAsync(string bundleRoot, CancellationToken ct) {
        var name = Path.GetFileName(bundleRoot.TrimEnd('/'));
        var target = Path.Combine(applicationsDir, name);
        if (Directory.Exists(target) || File.Exists(target))
            return new MoveOutcome(false, target, $"{name} already exists in {applicationsDir}. Open that copy instead.");

        var staging = Path.Combine(applicationsDir, $"{name}.staging-{Guid.NewGuid():N}");
        try {
            var copy = await runner.RunAsync("ditto", [bundleRoot, staging], new RunOptions(Timeout: CopyTimeout), ct).ConfigureAwait(false);
            if (copy.ExitCode != 0) return Fail(staging, $"Copying failed: {copy.Stderr.Trim()}");

            if (!File.Exists(Path.Combine(staging, "Contents", "Info.plist")) ||
                !File.Exists(Path.Combine(staging, "Contents", "MacOS", "Kurrent Capacitor")))
                return Fail(staging, "The copy is incomplete.");

            if (!promote(staging, target))
                return Fail(staging, $"{name} appeared in {applicationsDir} while copying. Open that copy instead.");

            return new MoveOutcome(true, target, null);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return Fail(staging, ex.Message);
        }
    }

    static MoveOutcome Fail(string staging, string error) {
        try {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        } catch (Exception) {
            // the staging copy is the only thing that could be left behind; reporting the move failure matters more
        }
        return new MoveOutcome(false, null, error);
    }

    /// A plain rename replaces an EMPTY existing directory; RENAME_EXCL fails on any existing entry.
    [SupportedOSPlatform("macos")]
    public static bool PromoteExclusive(string from, string to) => renamex_np(from, to, RENAME_EXCL) == 0;

    const uint RENAME_EXCL = 0x4;

    [LibraryImport("libc", EntryPoint = "renamex_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int renamex_np(string from, string to, uint flags);
}
