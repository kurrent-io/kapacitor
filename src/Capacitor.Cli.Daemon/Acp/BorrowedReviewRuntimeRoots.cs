namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The read-only paths a borrowed reviewer needs in order to START — its own program, its language
/// runtime, and that runtime's shared libraries — derived from the vendor binary rather than granted
/// as whole installation trees.
///
/// <para><b>Why not just grant the prefix.</b> The profile used to grant all of <c>/opt/homebrew</c>
/// and all of <c>/Library</c>. Both are data-bearing: on an ordinary developer machine
/// <c>/opt/homebrew/var</c> holds service databases and logs (a local Prometheus TSDB, for one) and
/// <c>/opt/homebrew/etc</c> holds service configuration. None of it is reachable through an ACP
/// interaction frame, so the <c>Fail</c> policy never fires — the OS boundary was the only thing
/// standing there, and it was open.</para>
///
/// <para>So the prefix is admitted by its SOFTWARE subdirectories only. <c>etc</c> and <c>var</c> are
/// never granted wholesale; the sole exception is the handful of package-installed crypto
/// configuration directories a TLS-using runtime reads during startup, named individually below —
/// without them a Homebrew Node aborts before it can speak a single ACP frame.</para>
///
/// <para><b>Fail-loud, not fail-open.</b> A runtime whose files fall outside these roots does not get
/// a silently widened profile: it fails to exec, with a dynamic-linker error naming the exact path.
/// That is diagnosable and safe, which a broader default would not be.</para>
/// </summary>
internal static class BorrowedReviewRuntimeRoots {
    /// <summary>Subdirectories of a Unix installation prefix that hold executables, libraries and
    /// package payloads. Deliberately omits <c>etc</c> and <c>var</c> — the config and data trees.</summary>
    static readonly string[] SoftwareSubdirectories = [
        "bin", "sbin", "lib", "libexec", "opt", "Cellar", "Frameworks", "share", "include"
    ];

    /// <summary>Package-installed crypto configuration read during runtime startup. Named entries
    /// UNDER <c>etc</c>, never <c>etc</c> itself: a Homebrew Node reads
    /// <c>etc/openssl@3/openssl.cnf</c> before <c>main</c> and aborts with an OpenSSL configuration
    /// error without it, while its siblings (<c>gitconfig</c>, service configs) stay unreadable.</summary>
    static readonly string[] CryptoConfigSubdirectories = [
        Path.Combine("etc", "openssl@3"),
        Path.Combine("etc", "openssl@1.1"),
        Path.Combine("etc", "ca-certificates"),
        Path.Combine("etc", "ssl")
    ];

    /// <summary>How far up from the binary to look for an installation prefix before giving up.</summary>
    const int MaxPrefixSearchDepth = 16;

    /// <summary>Read-only roots for the vendor at <paramref name="vendorBinaryPath"/>, deduplicated
    /// and containing only paths that exist.</summary>
    /// <param name="directoryExists">Directory probe. Production passes
    /// <see cref="Directory.Exists"/>; tests pass a fake so the layout rules are assertable against a
    /// synthetic prefix without creating one on disk.</param>
    internal static IReadOnlyList<string> Resolve(
            string vendorBinaryPath, Func<string, bool>? directoryExists = null) {
        var exists = directoryExists ?? Directory.Exists;
        var roots  = new List<string>();

        if (string.IsNullOrWhiteSpace(vendorBinaryPath)) return roots;

        // The RESOLVED binary, because the sandbox matches on resolved paths and a launcher on PATH
        // is routinely a symlink into the package that actually holds the program.
        var resolved = SandboxPaths.TryResolvePhysical(vendorBinaryPath) ?? vendorBinaryPath;
        var packageDirectory = TryGetDirectory(resolved);

        // The package directory itself, so an installation outside any recognizable prefix still
        // yields a readable program.
        if (packageDirectory is not null && !IsFilesystemRoot(packageDirectory) && exists(packageDirectory))
            roots.Add(packageDirectory);

        var prefix = FindInstallationPrefix(packageDirectory, exists);

        if (prefix is not null)
            foreach (var subdirectory in SoftwareSubdirectories.Concat(CryptoConfigSubdirectories)) {
                var path = Path.Combine(prefix, subdirectory);

                if (exists(path)) roots.Add(path);
            }

        return [.. roots.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The nearest ancestor that looks like a Unix installation prefix — one holding both
    /// <c>bin</c> and <c>lib</c>. That shape is what makes this vendor-neutral and install-method
    /// neutral: it finds <c>/opt/homebrew</c>, <c>/usr/local</c>, an nvm/volta node root, or a
    /// self-contained vendor directory without any of them being named here.</summary>
    static string? FindInstallationPrefix(string? start, Func<string, bool> exists) {
        var current = start;

        for (var depth = 0; depth < MaxPrefixSearchDepth && current is not null; depth++) {
            var parent = TryGetDirectory(current);

            // Stop at the filesystem root: "/" trivially has bin, and granting its software
            // subdirectories would readmit most of the machine.
            if (parent is null || parent == current || IsFilesystemRoot(parent)) return null;

            if (exists(Path.Combine(parent, "bin")) && exists(Path.Combine(parent, "lib")))
                return parent;

            current = parent;
        }

        return null;
    }

    static bool IsFilesystemRoot(string path) => SandboxPaths.IsFilesystemRoot(path);

    static string? TryGetDirectory(string path) {
        try {
            return Path.GetDirectoryName(path) is { Length: > 0 } directory ? directory : null;
        } catch (ArgumentException) {
            return null;
        }
    }
}
