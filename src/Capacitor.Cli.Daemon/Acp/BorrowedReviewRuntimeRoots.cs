using System.Collections.Immutable;
using Capacitor.Cli.Core;

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

    /// <summary>Installation prefixes whose layout has actually been MEASURED, and the only ones whose
    /// software subdirectories are granted recursively.
    ///
    /// <para>Holding <c>bin</c> and <c>lib</c> is a compatibility shape, not a trust boundary, and it is
    /// unsafe wherever it is applied: a configured vendor at <c>/private/tmp/reviewer/bin/copilot</c>
    /// beside a <c>lib</c>, or anything under <c>/Users/Shared</c> or a mounted work directory, would
    /// otherwise have had those trees granted recursively. Those locations are daemon-user-writable and
    /// outside home, so neither the root nor the home exclusion catches them.</para>
    ///
    /// <para>So the shape is used to FIND a candidate and this list decides whether it may be trusted.
    /// Only Apple Silicon Homebrew has been measured, and this feature is macOS/arm64-only, so the list
    /// has one entry. Adding a per-user layout (nvm, Volta, <c>~/.local</c>) means measuring it against a
    /// real vendor start — the same bar the platform gate is held to — not widening the classifier.</para>
    ///
    /// <para>A vendor installed anywhere else gets its executable literal and nothing more, so the
    /// launch fails loudly at exec rather than quietly reading adjacent files.</para></summary>
    internal static readonly ImmutableArray<string> MeasuredPrefixes = ["/opt/homebrew"];

    /// <summary>How far up from the binary to look for an installation prefix before giving up.</summary>
    const int MaxPrefixSearchDepth = 16;

    /// <summary>Read-only roots for the vendor at <paramref name="vendorBinaryPath"/>, deduplicated
    /// and containing only paths that exist.</summary>
    /// <param name="directoryExists">Directory probe. Production passes
    /// <see cref="Directory.Exists"/>; tests pass a fake so the layout rules are assertable against a
    /// synthetic prefix without creating one on disk.</param>
    /// <param name="home">The daemon user's home directory, which is never itself granted.</param>
    /// <param name="fileExists">File probe, for the crypto literals. Defaults to
    /// <see cref="File.Exists"/>.</param>
    /// <param name="measuredPrefixes">Test seam ONLY, for the synthetic layouts below. Production passes
    /// null and gets <see cref="MeasuredPrefixes"/>.</param>
    internal static BorrowedReviewRuntimeGrants Resolve(
            string vendorBinaryPath, UserHome home, Func<string, bool>? directoryExists = null,
            Func<string, bool>? fileExists = null, IReadOnlyList<string>? measuredPrefixes = null) {
        var dirExists  = directoryExists ?? Directory.Exists;
        var thisExists = fileExists ?? directoryExists ?? File.Exists;
        var roots      = new List<string>();
        var files      = new List<string>();

        // BOTH forms of home, because the candidate paths below are PHYSICAL (the vendor path is
        // symlink-resolved) while SpecialFolder.UserProfile is logical, and lexical normalization does
        // not bridge the two. On a host where home or an ancestor is a symlink — macOS ships
        // /home -> /System/Volumes/Data/home — a real prefix under home resolves to a physical path that
        // is not lexically "within" the logical home, and the under-home refusal below would silently
        // not apply. Comparing against both forms cannot be defeated by either one.
        var homeForms = HomeForms(home.Path);

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

        var prefix = FindInstallationPrefix(
            packageDirectory, dirExists, homeForms, measuredPrefixes ?? MeasuredPrefixes);

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
    /// neutral in HOW it searches — it finds a prefix without hard-coding a search path — but the
    /// candidate it returns must then appear in <see cref="MeasuredPrefixes"/>. Shape alone decides
    /// nothing: an unmeasured location yields no prefix, and therefore no recursive grant.</summary>
    static string? FindInstallationPrefix(
            string? start, Func<string, bool> exists, IReadOnlyList<string> home,
            IReadOnlyList<string> measured) {
        // No usable home form means the under-home refusal cannot be evaluated, so no prefix is safe.
        if (home.Count == 0) return null;

        var current = start;

        for (var depth = 0; depth < MaxPrefixSearchDepth && current is not null; depth++) {
            var parent = TryGetDirectory(current);

            // Stop, rather than continue upward: everything above an ungrantable root is broader still.
            if (parent is null || parent == current || IsUngrantableRoot(parent, home)) return null;

            if (exists(Path.Combine(parent, "bin")) && exists(Path.Combine(parent, "lib")))
                // `bin` + `lib` is a compatibility classifier, not a confidentiality-safe one, and BELOW
                // THE USER'S HOME it is not safe at all: a source repository or a mixed-use ~/toolbox
                // with ordinary bin/ and lib/ names matches it, and the exact-home exclusion does not
                // apply because the match is a descendant rather than home itself.
                //
                // So an under-home prefix is refused outright. The consequence is real and deliberate: a
                // per-user install (nvm, Volta, ~/.local) yields only the executable literal and the
                // launch then fails loudly at exec instead of quietly reading the user's files. Admitting
                // those layouts needs each one MEASURED — the same bar every other part of this feature is
                // held to — and only the Homebrew prefix has been.
                // Shape found a candidate; only a MEASURED prefix may be trusted with a recursive
                // grant. The under-home check is retained as defense in depth for any future measured
                // entry that happens to live below home.
                return home.Any(h => IsWithin(parent, h)) || !IsMeasured(parent, measured)
                    ? null
                    : parent;

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
    /// <para>This rejects home ITSELF; installations BENEATH home are refused separately, by
    /// <see cref="FindInstallationPrefix"/>. So a per-user prefix — <c>~/.local</c>,
    /// <c>~/.volta/tools/image/node/22</c>, an nvm root — does NOT work: it yields the executable
    /// literal only, and the launch then fails loudly at exec. That is deliberate, because none of those
    /// layouts appears in <see cref="MeasuredPrefixes"/>. Supporting one means measuring it.</para></summary>
    static bool IsUngrantableRoot(string path, IReadOnlyList<string> home) =>
        SandboxPaths.IsFilesystemRoot(path) || home.Any(h => IsSamePath(path, h));

    /// <summary>Home in every form a comparison might need: as configured, and symlink-resolved.
    ///
    /// <para>Returns EMPTY when home is blank or cannot be inspected, which makes
    /// <see cref="FindInstallationPrefix"/> refuse every prefix — fail closed. Treating an
    /// unresolvable home as "nothing is under home" would quietly disable the guard on exactly the
    /// hosts whose layout is unusual enough to be worth guarding.</para></summary>
    static IReadOnlyList<string> HomeForms(string? home) {
        if (string.IsNullOrWhiteSpace(home)) return [];

        var physical = SandboxPaths.TryResolvePhysical(home);

        if (physical is null) return [];

        return physical.Equals(home, StringComparison.Ordinal) ? [home] : [home, physical];
    }

    /// <summary>Whether <paramref name="candidate"/> is one of the measured prefixes, comparing both
    /// the configured and symlink-resolved forms so a linked install location still matches.</summary>
    static bool IsMeasured(string candidate, IReadOnlyList<string> measured) =>
        measured.Any(prefix =>
            IsSamePath(candidate, prefix) ||
            (SandboxPaths.TryResolvePhysical(prefix) is { } physical && IsSamePath(candidate, physical)));

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
