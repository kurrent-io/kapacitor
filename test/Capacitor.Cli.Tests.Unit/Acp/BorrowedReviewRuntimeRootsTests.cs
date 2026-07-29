using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Which installation paths a borrowed reviewer is allowed to read in order to start.
///
/// <para>These replaced whole-tree grants of <c>/opt/homebrew</c> and <c>/Library</c>. The rule that
/// matters is the negative one: software subdirectories are admitted, and the prefix's configuration
/// and data trees are not — on an ordinary developer machine <c>var</c> holds service databases and
/// logs and <c>etc</c> holds service configuration, and a code reviewer has no business in either.</para>
/// </summary>
public class BorrowedReviewRuntimeRootsTests {
    /// <summary>These fixtures are POSIX-absolute, and on Windows <see cref="Path.GetFullPath(string)"/>
    /// anchors such a path to the current drive (<c>/opt/hb</c> becomes <c>D:\opt\hb</c>), so every
    /// synthetic path would miss its fixture for a reason unrelated to the rules under test.
    ///
    /// <para>Skipped rather than made drive-aware because the resolver is macOS-only in production: it
    /// exists to feed a <c>sandbox-exec</c> profile, and the policy table has no Windows entry. The
    /// Linux CI leg runs these unchanged, so the rules stay covered.</para></summary>
    [Before(Test)]
    public void SkipOnWindows() =>
        Skip.When(OperatingSystem.IsWindows(),
                  "POSIX path fixtures; the resolver is only reachable on macOS.");

    /// <summary>A synthetic prefix laid out like Homebrew's, so the layout rules are assertable
    /// without depending on what happens to be installed on the host.</summary>
    static Func<string, bool> Prefix(string root) {
        string[] present = [
            root, $"{root}/bin", $"{root}/sbin", $"{root}/lib", $"{root}/libexec", $"{root}/opt",
            $"{root}/Cellar", $"{root}/Frameworks", $"{root}/share", $"{root}/include",
            $"{root}/etc", $"{root}/etc/openssl@3", $"{root}/etc/ca-certificates",
            $"{root}/etc/service-config", $"{root}/var", $"{root}/var/prometheus",
            $"{root}/lib/node_modules/@vendor/tool",
            // The crypto LITERALS the resolver grants, plus a sibling secret it must not.
            $"{root}/etc/openssl@3/openssl.cnf", $"{root}/etc/openssl@3/cert.pem",
            $"{root}/etc/openssl@3/private/server.key", $"{root}/etc/ca-certificates/cert.pem"
        ];

        return path => present.Contains(Posix(path));
    }

    /// <summary>Separators normalized to <c>/</c> so the POSIX-shaped fixtures below read the same on
    /// every platform. The resolver joins with <see cref="Path.Combine"/>, which emits <c>\</c> on
    /// Windows — the paths are equivalent, and asserting on the raw form would make these tests fail
    /// there for a reason that has nothing to do with the grant rules they exist to pin.</summary>
    static string Posix(string path) => path.Replace('\\', '/');

    /// <summary>Synthetic prefixes must be declared MEASURED, or the resolver correctly refuses them —
    /// shape alone no longer earns a recursive grant. Each caller names the prefix it is exercising.</summary>
    static BorrowedReviewRuntimeGrants Grants(
            string binary, Func<string, bool> exists, string userHome = "/Users/nobody",
            params string[] measured) =>
        BorrowedReviewRuntimeRoots.Resolve(
            binary, exists, userHome, measuredPrefixes: measured.Length == 0 ? ["/opt/hb"] : measured);

    static IReadOnlyList<string> Resolve(
            string binary, Func<string, bool> exists, string userHome = "/Users/nobody",
            params string[] measured) =>
        [.. Grants(binary, exists, userHome, measured).Directories.Select(Posix)];

    static IReadOnlyList<string> ResolveFiles(
            string binary, Func<string, bool> exists, string userHome = "/Users/nobody",
            params string[] measured) =>
        [.. Grants(binary, exists, userHome, measured).Files.Select(Posix)];

    static IReadOnlyList<string> Resolve(string binary, string prefixRoot) =>
        Resolve(binary, Prefix(prefixRoot), "/Users/nobody", prefixRoot);

    [Test]
    public async Task The_software_subdirectories_of_the_discovered_prefix_are_granted() {
        var roots = Resolve("/opt/hb/lib/node_modules/@vendor/tool/loader.js", "/opt/hb");

        foreach (var expected in new[] { "bin", "sbin", "lib", "libexec", "opt", "Cellar",
                                         "Frameworks", "include" })
            await Assert.That(roots).Contains($"/opt/hb/{expected}");

        // NOT share: under a per-user prefix such as ~/.local that is the XDG application-data tree,
        // not software payload. Probed as unnecessary for the vendor to start.
        await Assert.That(roots).DoesNotContain("/opt/hb/share");
    }

    /// <summary>The whole point. <c>etc</c> and <c>var</c> are never granted as trees, and neither is
    /// a config directory that merely happens to live under <c>etc</c>.</summary>
    [Test]
    [Arguments("/opt/hb/etc")]
    [Arguments("/opt/hb/var")]
    [Arguments("/opt/hb/var/prometheus")]
    [Arguments("/opt/hb/etc/service-config")]
    public async Task The_prefixes_config_and_data_trees_are_never_granted(string forbidden) {
        var roots = Resolve("/opt/hb/lib/node_modules/@vendor/tool/loader.js", "/opt/hb");

        await Assert.That(roots).DoesNotContain(forbidden);
    }

    /// <summary>The narrow exception, and it is load-bearing: a Homebrew Node reads
    /// <c>etc/openssl@3/openssl.cnf</c> before <c>main</c> and aborts with an OpenSSL configuration
    /// error without it. Probed — this was the last grant standing between the narrowed profile and a
    /// vendor that would not start.
    ///
    /// <para>Granted as individual FILES, not as their directories. An earlier revision granted
    /// <c>etc/openssl@3</c> wholesale, which also admits <c>certs/</c>, <c>misc/</c> and by convention
    /// <c>private/</c> — locally managed certificates and their keys. The fixture plants a
    /// <c>private/server.key</c> so that regression cannot pass unnoticed.</para></summary>
    [Test]
    public async Task Package_installed_crypto_config_is_granted_as_files_not_directories() {
        var binary = "/opt/hb/lib/node_modules/@vendor/tool/loader.js";
        var files  = ResolveFiles(binary, Prefix("/opt/hb"), "/Users/nobody", "/opt/hb");
        var roots  = Resolve(binary, "/opt/hb");

        await Assert.That(files).Contains("/opt/hb/etc/openssl@3/openssl.cnf");
        await Assert.That(files).Contains("/opt/hb/etc/ca-certificates/cert.pem");

        // The containing directories are NOT granted, so the adjacent private key is unreachable.
        await Assert.That(roots).DoesNotContain("/opt/hb/etc/openssl@3");
        await Assert.That(roots).DoesNotContain("/opt/hb/etc/ca-certificates");
        await Assert.That(files).DoesNotContain("/opt/hb/etc/openssl@3/private/server.key");
    }

    /// <summary>A bare command name grants nothing.
    ///
    /// <para>The severe route review found: every vendor path defaults to a bare command
    /// (<c>"copilot"</c>), and <see cref="Path.GetFullPath(string)"/> resolves that against the DAEMON'S
    /// CURRENT DIRECTORY — making that directory the package directory and granting it recursively,
    /// while sandbox-exec separately executed the real binary from PATH. A daemon started from a source
    /// checkout or a home directory would have handed the reviewer an unrelated tree with nothing in the
    /// profile looking wrong. Callers now resolve through <c>CliResolver</c> first; this refuses the
    /// class regardless.</para></summary>
    [Test]
    [Arguments("copilot")]
    [Arguments("./copilot")]
    [Arguments("bin/copilot")]
    public async Task A_bare_or_relative_command_name_grants_nothing(string binary) {
        var grants = BorrowedReviewRuntimeRoots.Resolve(binary, _ => true, userHome: "/Users/nobody");

        await Assert.That(grants.Directories).IsEmpty()
            .Because("a relative path would resolve against the daemon's current directory");
        await Assert.That(grants.Files).IsEmpty();
    }

    /// <summary>The program is granted as a FILE, and its package directory only because that
    /// directory sits inside a software root this prefix already admits (<c>lib</c>).</summary>
    [Test]
    public async Task The_program_is_granted_as_a_file_and_its_package_only_inside_a_software_root() {
        var binary = "/opt/hb/lib/node_modules/@vendor/tool/loader.js";

        await Assert.That(ResolveFiles(binary, Prefix("/opt/hb"), "/Users/nobody", "/opt/hb")).Contains(binary);
        await Assert.That(Resolve(binary, "/opt/hb")).Contains("/opt/hb/lib/node_modules/@vendor/tool");
    }

    /// <summary>An executable in an ordinary user directory grants the FILE and NOT its parent.
    ///
    /// <para>Review's unresolved half of the cwd finding: resolving the path fixed where it pointed but
    /// not what got granted, so a real executable at <c>~/bin/copilot</c> still handed the reviewer all
    /// of <c>~/bin</c> — unrelated scripts, credentials, whatever else lives there — and neither the
    /// filesystem-root nor the exact-home exclusion catches <c>~/bin</c>, because it is neither.</para></summary>
    [Test]
    public async Task An_executable_in_a_plain_directory_does_not_grant_that_directory() {
        const string home = "/Users/dev";
        // ~/bin exists but there is no sibling lib, so no prefix is recognized.
        var grants = BorrowedReviewRuntimeRoots.Resolve(
            $"{home}/bin/copilot",
            path => Posix(path) is $"{home}/bin" or home,
            userHome: home);

        await Assert.That(grants.Files.Select(Posix)).Contains($"{home}/bin/copilot");
        await Assert.That(grants.Directories).IsEmpty()
            .Because("the containing directory is user data, not a recognized package layout");
    }

    /// <summary>A prefix is recognized by holding both <c>bin</c> and <c>lib</c>, which is what makes
    /// this neutral across install methods — Homebrew, <c>/usr/local</c>, an nvm/volta node root, or a
    /// self-contained vendor directory — without any of them being named in the code.
    ///
    /// <para>The fixture deliberately avoids <c>/home</c>, which is a symlink to
    /// <c>/System/Volumes/Data/home</c> on macOS: the resolver would correctly resolve it and then miss
    /// this synthetic layout, failing for a reason that is about the host rather than about the rule.
    /// (That it does resolve symlinked ancestors is load-bearing for the sandbox and covered elsewhere.)</para></summary>
    [Test]
    public async Task A_prefix_is_discovered_by_shape_not_by_name() {
        const string prefix = "/opt/toolchains/node/22";

        var roots = Resolve($"{prefix}/lib/node_modules/@vendor/tool/loader.js", Prefix(prefix),
                            "/Users/nobody", prefix);

        await Assert.That(roots).Contains($"{prefix}/bin");
        await Assert.That(roots).Contains($"{prefix}/lib");
    }

    /// <summary>An UNMEASURED prefix earns no recursive grant, however convincing its shape.
    ///
    /// <para>Review's scenario: a configured vendor at <c>/private/tmp/reviewer/bin/copilot</c> beside a
    /// <c>lib</c> matches the classifier while being a daemon-user-writable scratch directory outside
    /// home, so neither the root nor the home exclusion catches it. Holding <c>bin</c> and <c>lib</c> is
    /// a compatibility shape, not a trust boundary — it finds a candidate, and
    /// <c>MeasuredPrefixes</c> decides whether that candidate is trusted.</para></summary>
    [Test]
    [Arguments("/private/tmp/reviewer")]
    [Arguments("/Users/Shared/tools")]
    [Arguments("/Volumes/work/toolchain")]
    public async Task An_unmeasured_prefix_earns_no_recursive_grant(string prefix) {
        string[] present = [prefix, $"{prefix}/bin", $"{prefix}/lib", $"{prefix}/share"];

        var grants = BorrowedReviewRuntimeRoots.Resolve(
            $"{prefix}/bin/copilot", path => present.Contains(Posix(path)),
            userHome: "/Users/nobody", measuredPrefixes: ["/opt/homebrew"]);

        await Assert.That(grants.Directories).IsEmpty()
            .Because($"{prefix} has a plausible layout but has never been measured");
        // Still granted as a file, so the launch fails loudly at exec rather than silently.
        await Assert.That(grants.Files.Select(Posix)).Contains($"{prefix}/bin/copilot");
    }

    /// <summary>The shipped trust boundary, pinned by VALUE.
    ///
    /// <para>An earlier version of this test asserted only that <c>/opt/homebrew</c> was admitted and one
    /// arbitrary path refused — which stays green when a second entry is added, so it did not pin the
    /// thing it claimed to. This list is now the confidentiality boundary, so any expansion has to fail
    /// here and be looked at, whatever the new entry happens to be.</para></summary>
    [Test]
    public async Task The_measured_prefix_list_is_exactly_homebrew() {
        await Assert.That(BorrowedReviewRuntimeRoots.MeasuredPrefixes.ToArray())
            .IsEquivalentTo(["/opt/homebrew"])
            .Because("every entry here grants software trees recursively and must be measured first");
    }

    /// <summary>...and the list is actually consulted, in both directions, through the production path.</summary>
    [Test]
    public async Task The_real_resolver_admits_the_measured_prefix_and_refuses_a_lookalike() {
        string[] present = ["/opt/homebrew", "/opt/homebrew/bin", "/opt/homebrew/lib",
                            "/opt/other", "/opt/other/bin", "/opt/other/lib"];

        var admitted = BorrowedReviewRuntimeRoots.Resolve(
            "/opt/homebrew/bin/copilot", path => present.Contains(Posix(path)), userHome: "/Users/nobody");
        var refused = BorrowedReviewRuntimeRoots.Resolve(
            "/opt/other/bin/copilot", path => present.Contains(Posix(path)), userHome: "/Users/nobody");

        await Assert.That(admitted.Directories.Select(Posix)).Contains("/opt/homebrew/lib");
        await Assert.That(refused.Directories).IsEmpty();
    }

    /// <summary>Never the filesystem root, in EITHER of the two ways it can be reached.
    ///
    /// <para>An earlier revision of this test asserted only that the root's software subdirectories
    /// (<c>/bin</c>, <c>/lib</c>, …) were absent, and it passed against a build that granted
    /// <c>(subpath "/")</c> outright — the package-directory of a binary resolving to <c>/copilot</c>
    /// is <c>/</c>. That grant is the entire filesystem, so every named-tree containment assertion
    /// elsewhere would also have passed while the boundary was gone. Both routes are now asserted, and
    /// the root itself first.</para></summary>
    [Test]
    [Arguments("/copilot")]
    [Arguments("/opt")]
    public async Task The_filesystem_root_is_never_granted(string binary) {
        var roots = Resolve(binary, _ => true);

        // Route 1: the root as a package directory.
        await Assert.That(roots).DoesNotContain("/");
        await Assert.That(roots).DoesNotContain(Path.GetPathRoot(Path.GetFullPath(binary))!);
        // Route 2: the root treated as an installation prefix.
        await Assert.That(roots).DoesNotContain("/bin");
        await Assert.That(roots).DoesNotContain("/lib");
        await Assert.That(roots).DoesNotContain("/etc");
        await Assert.That(roots).DoesNotContain("/var");
    }

    /// <summary>The OTHER ungrantable root, and the one that matters most here: a home directory
    /// holding <c>bin</c> and <c>lib</c> is an ordinary shape, and it matches the prefix rule. Granting
    /// it would hand over <c>~/bin</c>, <c>~/lib</c>, <c>~/share</c>, <c>~/include</c> — user data, and
    /// the exact class of grant this change exists to remove. The PR claims the profile grants nothing
    /// under the user's home; without this it did not.</summary>
    [Test]
    public async Task The_user_home_is_never_treated_as_a_prefix() {
        const string home = "/Users/dev";
        string[] present = [
            home, $"{home}/bin", $"{home}/lib", $"{home}/share", $"{home}/include",
            $"{home}/lib/node_modules/@vendor/tool"
        ];

        var roots = Resolve(
            $"{home}/lib/node_modules/@vendor/tool/loader.js",
            path => present.Contains(Posix(path)), userHome: home);

        await Assert.That(roots).DoesNotContain(home);
        await Assert.That(roots).DoesNotContain($"{home}/bin");
        await Assert.That(roots).DoesNotContain($"{home}/lib");
        await Assert.That(roots).DoesNotContain($"{home}/share");
        await Assert.That(roots).DoesNotContain($"{home}/include");
    }

    /// <summary>Home as the PACKAGE directory — the second route to the same grant, which a
    /// prefix-only guard would miss. A binary resolving to <c>~/copilot</c> would otherwise grant the
    /// whole home directory.</summary>
    [Test]
    public async Task The_user_home_is_never_granted_as_a_package_directory() {
        var roots = Resolve("/Users/dev/copilot", _ => true, userHome: "/Users/dev");

        await Assert.That(roots).DoesNotContain("/Users/dev");
    }

    /// <summary>A prefix BENEATH the user's home is refused, even though it matches the shape rule.
    ///
    /// <para><c>bin</c> + <c>lib</c> is a compatibility classifier, not a confidentiality-safe one, and
    /// below home it is not safe at all: a source repository or a mixed-use <c>~/toolbox</c> with
    /// ordinary <c>bin/</c> and <c>lib/</c> names matches it, and the exact-home exclusion does not
    /// apply because the match is a descendant rather than home itself. An earlier revision granted
    /// exactly that, and the test that was supposed to cover per-user installs removed the sibling
    /// <c>lib</c>, so it could not see the route.</para>
    ///
    /// <para>The cost is deliberate and stated rather than hidden: a per-user install (nvm, Volta,
    /// <c>~/.local</c>) gets only the executable literal, so the launch fails loudly at exec instead of
    /// quietly reading the user's files. Admitting one of those layouts requires MEASURING it, the same
    /// bar the rest of this feature is held to; only the Homebrew prefix has been measured.</para></summary>
    [Test]
    [Arguments("/Users/dev/.local")]
    [Arguments("/Users/dev/toolbox")]
    public async Task A_prefix_beneath_the_user_home_is_refused(string prefix) {
        const string home = "/Users/dev";
        string[] present = [home, prefix, $"{prefix}/bin", $"{prefix}/lib", $"{prefix}/share",
                            $"{prefix}/lib/node_modules/@vendor/tool"];

        // Declared measured on purpose: the refusal must come from the HOME rule, not from the
        // measured-list rule, or this test would pass for the wrong reason.
        var grants = BorrowedReviewRuntimeRoots.Resolve(
            $"{prefix}/lib/node_modules/@vendor/tool/loader.js",
            path => present.Contains(Posix(path)), userHome: home, measuredPrefixes: [prefix]);

        await Assert.That(grants.Directories).IsEmpty()
            .Because($"{prefix} matches bin+lib but is user-controlled, so it is not a safe prefix");
        // The program itself is still granted, so the failure is a loud exec error rather than silence.
        await Assert.That(grants.Files.Select(Posix))
            .Contains($"{prefix}/lib/node_modules/@vendor/tool/loader.js");
    }

    /// <summary>The under-home refusal survives a symlinked home, using REAL directories and a REAL
    /// symlink rather than a fake probe.
    ///
    /// <para>Review's finding: the vendor path is symlink-resolved but home was compared as the logical
    /// string, and lexical normalization does not bridge the two. With home reached through a symlink,
    /// the physical candidate is not lexically "within" the logical home, so the refusal silently did
    /// not apply — on macOS exactly, which ships <c>/home -> /System/Volumes/Data/home</c>.</para>
    ///
    /// <para>The assertion on the LOGICAL form is what makes this test meaningful: it confirms the two
    /// forms genuinely differ here, so the refusal below can only come from comparing the physical
    /// one.</para></summary>
    [Test]
    public async Task A_prefix_beneath_a_symlinked_home_is_still_refused() {
        Skip.When(OperatingSystem.IsWindows(), "needs POSIX symlinks");

        var root = Directory.CreateTempSubdirectory("kcap-symlink-home").FullName;
        try {
            var real   = Path.Combine(root, "real");
            var linked = Path.Combine(root, "linked");
            var prefix = Path.Combine(real, "dev", "toolbox");
            Directory.CreateDirectory(Path.Combine(prefix, "bin"));
            Directory.CreateDirectory(Path.Combine(prefix, "lib"));
            Directory.CreateSymbolicLink(linked, real);

            var logicalHome = Path.Combine(linked, "dev");
            var binary      = Path.Combine(prefix, "bin", "copilot");
            await File.WriteAllTextAsync(binary, "#!/bin/sh\n");

            // The two forms really do differ, or this test would prove nothing.
            await Assert.That(Path.GetFullPath(prefix)
                .StartsWith(Path.GetFullPath(logicalHome), StringComparison.Ordinal)).IsFalse();

            var grants = BorrowedReviewRuntimeRoots.Resolve(
                binary, userHome: logicalHome, measuredPrefixes: [prefix]);

            await Assert.That(grants.Directories).IsEmpty()
                .Because("the prefix is beneath home once home is resolved to its physical form");
        } finally {
            try { Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>An unusable home fails CLOSED: no home form means the under-home rule cannot be
    /// evaluated, so no prefix is safe to admit.</summary>
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task An_unusable_home_admits_no_prefix(string home) {
        var grants = BorrowedReviewRuntimeRoots.Resolve(
            "/opt/hb/lib/node_modules/@vendor/tool/loader.js", Prefix("/opt/hb"), userHome: home,
            measuredPrefixes: ["/opt/hb"]);

        await Assert.That(grants.Directories).IsEmpty();
    }

    /// <summary>The same hole one level down: a real installation under <c>/opt/vendor</c> must still
    /// resolve, so the root exclusion must not be implemented by refusing shallow paths generally.</summary>
    [Test]
    public async Task A_shallow_but_non_root_installation_still_resolves() {
        var roots = Resolve(
            "/opt/vendor/libexec/tool",
            path => Posix(path) is "/opt/vendor/bin" or "/opt/vendor/lib" or "/opt/vendor/libexec",
            "/Users/nobody", "/opt/vendor");

        await Assert.That(roots).Contains("/opt/vendor/bin");
        await Assert.That(roots).Contains("/opt/vendor/lib");
        await Assert.That(roots).DoesNotContain("/");
    }

    /// <summary>An unresolvable or empty binary path yields no grants rather than a widened profile —
    /// the launch then fails loudly at exec, which is the safe direction.</summary>
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task A_missing_binary_path_grants_nothing(string path) {
        await Assert.That(Resolve(path, _ => true)).IsEmpty();
    }

    [Test]
    public async Task Only_paths_that_exist_are_granted() {
        var roots = Resolve(
            "/opt/hb/lib/node_modules/@vendor/tool/loader.js",
            path => Posix(path) is "/opt/hb/bin" or "/opt/hb/lib"
                                or "/opt/hb/lib/node_modules/@vendor/tool",
            "/Users/nobody", "/opt/hb");

        await Assert.That(roots).Contains("/opt/hb/bin");
        await Assert.That(roots).DoesNotContain("/opt/hb/Cellar");
        await Assert.That(roots).DoesNotContain("/opt/hb/share");
    }

    [Test]
    public async Task Results_are_deduplicated() {
        var roots = Resolve("/opt/hb/lib/node_modules/@vendor/tool/loader.js", "/opt/hb");

        await Assert.That(roots.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(roots.Count);
    }

    /// <summary>Against the real installation, if there is one: the resolved roots must actually
    /// contain the program that will be executed, or the launch cannot start.</summary>
    [Test]
    public async Task The_real_vendor_installation_resolves_roots_covering_its_own_program() {
        Skip.Unless(RuntimeInformation.IsOSPlatform(OSPlatform.OSX), "macOS layout");
        const string binary = "/opt/homebrew/bin/copilot";
        Skip.Unless(File.Exists(binary), "Copilot CLI not installed on this host");

        var roots    = BorrowedReviewRuntimeRoots.Resolve(binary).Directories;
        var resolved = new FileInfo(binary).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? binary;

        await Assert.That(roots.Any(root =>
            resolved.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                StringComparison.Ordinal))).IsTrue()
            .Because($"no resolved runtime root covers the real program at {resolved}");
        await Assert.That(roots).DoesNotContain("/opt/homebrew/var");
        await Assert.That(roots).DoesNotContain("/opt/homebrew/etc");
        await Assert.That(roots).DoesNotContain("/opt/homebrew/share");
    }
}
