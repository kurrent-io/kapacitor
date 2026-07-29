namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The read-only paths a borrowed reviewer needs in order to START — its own program, its language
/// runtime, and that runtime's shared libraries — derived from the vendor binary rather than granted
/// as whole installation trees.
///
/// <para>An installation prefix is admitted by its SOFTWARE subdirectories only: <c>etc</c> and
/// <c>var</c> are data-bearing (service configuration, service databases, logs) and reachable with no
/// ACP interaction frame, so only the OS boundary stands there. The one exception is package-installed
/// crypto configuration, named individually below, without which a Homebrew Node aborts before it can
/// speak a frame.</para>
///
/// <para>A runtime whose files fall outside these roots is not given a silently widened profile — it
/// fails to exec with a dynamic-linker error naming the path.</para>
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
    /// <param name="userHome">The daemon user's home directory, which is never itself granted. Tests
    /// pass an explicit value so the rule is assertable without depending on the real <c>HOME</c>.</param>
    internal static IReadOnlyList<string> Resolve(
            string vendorBinaryPath, Func<string, bool>? directoryExists = null, string? userHome = null) {
        var exists = directoryExists ?? Directory.Exists;
        var home   = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots  = new List<string>();

        if (string.IsNullOrWhiteSpace(vendorBinaryPath)) return roots;

        // The RESOLVED binary, because the sandbox matches on resolved paths and a launcher on PATH
        // is routinely a symlink into the package that actually holds the program.
        var resolved = SandboxPaths.TryResolvePhysical(vendorBinaryPath) ?? vendorBinaryPath;
        var packageDirectory = TryGetDirectory(resolved);

        // The package directory itself, so an installation outside any recognizable prefix still
        // yields a readable program.
        if (packageDirectory is not null && !IsUngrantableRoot(packageDirectory, home) && exists(packageDirectory))
            roots.Add(packageDirectory);

        var prefix = FindInstallationPrefix(packageDirectory, exists, home);

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
    static string? FindInstallationPrefix(string? start, Func<string, bool> exists, string home) {
        var current = start;

        for (var depth = 0; depth < MaxPrefixSearchDepth && current is not null; depth++) {
            var parent = TryGetDirectory(current);

            // Stop, rather than continue upward: everything above an ungrantable root is broader still.
            if (parent is null || parent == current || IsUngrantableRoot(parent, home)) return null;

            if (exists(Path.Combine(parent, "bin")) && exists(Path.Combine(parent, "lib")))
                return parent;

            current = parent;
        }

        return null;
    }

    /// <summary>Roots that must never become a grant, however the walk arrives at them.
    ///
    /// <para>Two of them, and both are reachable. <c>/</c> trivially holds <c>bin</c>, so a binary
    /// resolving to <c>/copilot</c> would make the filesystem root its package directory. And a home
    /// directory holding <c>bin</c> and <c>lib</c> — an ordinary shape — would match the prefix rule
    /// and grant <c>~/bin</c>, <c>~/lib</c>, <c>~/share</c>, <c>~/include</c>: user data, and the exact
    /// class of grant this change exists to remove.</para>
    ///
    /// <para>Note this rejects home ITSELF, not installations beneath it: <c>~/.local</c> or
    /// <c>~/.volta/tools/image/node/22</c> are still valid prefixes, so a per-user install keeps
    /// working.</para></summary>
    static bool IsUngrantableRoot(string path, string home) =>
        SandboxPaths.IsFilesystemRoot(path) || IsSamePath(path, home);

    static bool IsSamePath(string a, string b) {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        try {
            return string.Equals(Normalize(a), Normalize(b), comparison);
        } catch (Exception) {
            return false;
        }

        static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }

    static string? TryGetDirectory(string path) {
        try {
            return Path.GetDirectoryName(path) is { Length: > 0 } directory ? directory : null;
        } catch (ArgumentException) {
            return null;
        }
    }
}
