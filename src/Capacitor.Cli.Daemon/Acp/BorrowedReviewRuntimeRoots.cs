namespace Capacitor.Cli.Daemon.Acp;

/// <summary>Read-only grants the vendor needs to start: whole <paramref name="Directories"/> for
/// software payload, individual <paramref name="Files"/> where a directory would admit adjacent
/// secrets.</summary>
internal readonly record struct BorrowedReviewRuntimeGrants(
    IReadOnlyList<string> Directories, IReadOnlyList<string> Files
) {
    internal static BorrowedReviewRuntimeGrants None => new([], []);
}

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
    /// package payloads. Deliberately omits <c>etc</c> and <c>var</c> — the config and data trees —
    /// and also <c>share</c>, which under a per-user prefix such as <c>~/.local</c> is the XDG
    /// application-data tree rather than software payload. Probed: the vendor starts without it.</summary>
    static readonly string[] SoftwareSubdirectories = [
        "bin", "sbin", "lib", "libexec", "opt", "Cellar", "Frameworks", "include"
    ];

    /// <summary>Individual package-installed crypto FILES read during runtime startup — not their
    /// directories.
    ///
    /// <para>A Homebrew Node reads <c>etc/openssl@3/openssl.cnf</c> before <c>main</c> and aborts with
    /// an OpenSSL configuration error without it. Granting the containing directory instead would admit
    /// <c>certs/</c>, <c>misc/</c> and by convention <c>private/</c> — locally managed certificates and
    /// their keys. Probed: these four literals are sufficient. <c>cert.pem</c> appears twice because
    /// the <c>openssl@3</c> copy is a symlink into <c>ca-certificates</c> and the sandbox matches the
    /// resolved path.</para></summary>
    static readonly string[] CryptoConfigFiles = [
        Path.Combine("etc", "openssl@3", "openssl.cnf"),
        Path.Combine("etc", "openssl@3", "cert.pem"),
        Path.Combine("etc", "openssl@1.1", "openssl.cnf"),
        Path.Combine("etc", "ca-certificates", "cert.pem")
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
    /// <param name="fileExists">File probe, for the crypto literals. Defaults to
    /// <see cref="File.Exists"/>.</param>
    internal static BorrowedReviewRuntimeGrants Resolve(
            string vendorBinaryPath, Func<string, bool>? directoryExists = null, string? userHome = null,
            Func<string, bool>? fileExists = null) {
        var dirExists  = directoryExists ?? Directory.Exists;
        var thisExists = fileExists ?? directoryExists ?? File.Exists;
        var home       = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots      = new List<string>();
        var files      = new List<string>();

        // A bare command name — the shipped default for every vendor path — must never reach here:
        // Path.GetFullPath would resolve it against the DAEMON'S CURRENT DIRECTORY, making that
        // directory the "package directory" and granting it recursively. A daemon started from a source
        // checkout or a home directory would hand the reviewer an unrelated tree, and nothing about the
        // profile would look wrong. Callers resolve through CliResolver first; this refuses the class
        // outright rather than trusting them to.
        if (string.IsNullOrWhiteSpace(vendorBinaryPath) || !Path.IsPathFullyQualified(vendorBinaryPath))
            return BorrowedReviewRuntimeGrants.None;

        // The RESOLVED binary, because the sandbox matches on resolved paths and a launcher on PATH
        // is routinely a symlink into the package that actually holds the program.
        var resolved = SandboxPaths.TryResolvePhysical(vendorBinaryPath) ?? vendorBinaryPath;
        var packageDirectory = TryGetDirectory(resolved);

        // The program itself, always, and as a LITERAL. Granting its containing directory instead
        // would grant whatever else happens to live beside it — for an executable at ~/bin/copilot
        // that is the user's whole scripts directory, which the root and home exclusions do not catch
        // because ~/bin is neither.
        files.Add(resolved);

        var prefix = FindInstallationPrefix(packageDirectory, dirExists, home);

        if (prefix is not null) {
            foreach (var subdirectory in SoftwareSubdirectories) {
                var path = Path.Combine(prefix, subdirectory);

                if (dirExists(path)) roots.Add(path);
            }

            foreach (var file in CryptoConfigFiles) {
                var path = Path.Combine(prefix, file);

                if (thisExists(path)) files.Add(path);
            }

            // The package payload — a node package needs its whole directory, not just its entry
            // script — but ONLY once it is inside a software root this prefix already admits. An
            // installation outside any recognizable layout gets the executable literal and nothing
            // else, so a missing sibling fails loudly at exec instead of widening the profile.
            if (packageDirectory is not null &&
                roots.Any(root => IsWithin(packageDirectory, root)) &&
                dirExists(packageDirectory))
                roots.Add(packageDirectory);
        }

        return new([.. roots.Distinct(StringComparer.Ordinal)], [.. files.Distinct(StringComparer.Ordinal)]);
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

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="root"/> or sits beneath it.</summary>
    static bool IsWithin(string candidate, string root) {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar);

        return candidate.Equals(trimmed, comparison)
            || candidate.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison);
    }

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
