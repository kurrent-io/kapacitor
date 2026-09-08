using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Harness.Cursor;

internal sealed record CursorBorrowedReviewArtifact(string Version, string LauncherPath, string BundleDigest);

/// <summary>
/// A RECORD of the last Cursor build the maintainer validation workflow probed for borrowed-review
/// behavior — reads, containment, and process boundary. <b>It gates nothing.</b> A non-match is the
/// expected steady state (Cursor auto-updates) and is not an error: borrowed-review capability is
/// advertised for whatever build is installed, and a borrowed launch spawns the ordinary configured
/// binary. No caller may use <see cref="TryMatchValidatedBuild"/> to decide capability, to select a
/// launcher path, or to derive any runtime state; its only production caller is the manually
/// invoked maintainer validation workflow. See
/// docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md.
///
/// <para><b>Version form.</b> <see cref="Version"/> is the <i>version-directory name</i> the launcher
/// resolves into (<c>~/.local/share/cursor-agent/versions/&lt;name&gt;/cursor-agent</c>) — that is
/// what <see cref="TryMatchValidatedBuild"/> compares. Observed on the installed macOS build,
/// <c>cursor-agent --version</c> prints exactly the same string (<c>2026.07.23-e383d2b</c>), so this
/// form and the <c>CliVersion</c> the daemon probes and logs at startup agree today. They remain
/// independent facts — a future Cursor release printing a bare semver would diverge from the
/// directory name without anything being wrong, and nothing in production compares them.</para>
///
/// <para>Cursor auto-updated past this record on 2026-07-23, so the digests below describe an older
/// build than the one most machines run. Under trust-by-default that is a stale note, not a fault —
/// refresh it by re-running the maintainer probe suite against the installed build, never by
/// recomputing the hashes alone (that would record a validation that never happened).</para>
/// </summary>
internal static class CursorBorrowedReviewValidation {
    internal const string Version = "2026.07.20-8cc9c0b";
    internal const string LauncherSha256 = "eed61c5224668c9236334c4c68936a16aecc37374b592f59e31eb50433817831";
    // SHA-256 of sorted UTF-8 lines: "<file-sha256>  ./<relative-path>\n".
    internal const string BundleDigest = "1dd66852ef6c94a0344226fa733f6fc1f3552a8ccf16dd000e1e38134575e10b";
    internal const string Containment = "independent-snapshot";

    /// <summary>Reports whether the configured launcher resolves to the exact build recorded above.
    /// Informational only — see the type-level remarks. Returns <see langword="null"/> for any
    /// mismatch, and also on any platform other than macOS/arm64, which is where the maintainer
    /// probe suite runs; neither outcome says anything about whether borrowed review is
    /// supported.</summary>
    internal static CursorBorrowedReviewArtifact? TryMatchValidatedBuild(CliResolver cli, string configuredPath) {
        if (!OperatingSystem.IsMacOS()) return null;
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return null;
        try {
            var resolved = cli.ResolveExecutable(configuredPath);
            if (resolved is null) return null;
            var launcher = ResolveFinalLink(resolved);
            var versionDir = Directory.GetParent(launcher)?.FullName;
            if (versionDir is null || !Path.GetFileName(versionDir).Equals(Version, StringComparison.Ordinal)) return null;
            if (!SafeUnixPath(launcher, versionDir)) return null;
            if (!Sha256File(launcher).Equals(LauncherSha256, StringComparison.Ordinal)) return null;
            var digest = ComputeBundleDigest(versionDir);
            return digest.Equals(BundleDigest, StringComparison.Ordinal)
                ? new CursorBorrowedReviewArtifact(Version, launcher, digest)
                : null;
        } catch {
            return null;
        }
    }

    internal static string ComputeBundleDigest(string versionDir) {
        var lines = Directory.EnumerateFiles(versionDir, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(versionDir, path).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(x => !x.Relative.Equals(".running", StringComparison.Ordinal) &&
                        !x.Relative.StartsWith(".running/", StringComparison.Ordinal))
            .OrderBy(x => x.Relative, StringComparer.Ordinal)
            .Select(x => $"{Sha256File(x.Path)}  ./{x.Relative}\n");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in lines) hash.AppendData(Encoding.UTF8.GetBytes(line));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    static string ResolveFinalLink(string path) {
        var info = new FileInfo(path);
        var final = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.GetFullPath(final?.FullName ?? info.FullName);
    }

    [SupportedOSPlatform("macos")]
    static bool SafeUnixPath(string launcher, string versionDir) {
        var paths = new List<string> { launcher };
        for (DirectoryInfo? dir = new(versionDir); dir is not null; dir = dir.Parent)
            paths.Add(dir.FullName);
        foreach (var path in paths) {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
        }
        return true;
    }

    static string Sha256File(string path) {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
