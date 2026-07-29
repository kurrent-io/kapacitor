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
    /// <summary>A synthetic prefix laid out like Homebrew's, so the layout rules are assertable
    /// without depending on what happens to be installed on the host.</summary>
    static Func<string, bool> Prefix(string root) {
        string[] present = [
            root, $"{root}/bin", $"{root}/sbin", $"{root}/lib", $"{root}/libexec", $"{root}/opt",
            $"{root}/Cellar", $"{root}/Frameworks", $"{root}/share", $"{root}/include",
            $"{root}/etc", $"{root}/etc/openssl@3", $"{root}/etc/ca-certificates",
            $"{root}/etc/service-config", $"{root}/var", $"{root}/var/prometheus",
            $"{root}/lib/node_modules/@vendor/tool"
        ];

        return path => present.Contains(path.Replace('\\', '/'));
    }

    static IReadOnlyList<string> Resolve(string binary, string prefixRoot) =>
        BorrowedReviewRuntimeRoots.Resolve(binary, Prefix(prefixRoot));

    [Test]
    public async Task The_software_subdirectories_of_the_discovered_prefix_are_granted() {
        var roots = Resolve("/opt/hb/lib/node_modules/@vendor/tool/loader.js", "/opt/hb");

        foreach (var expected in new[] { "bin", "sbin", "lib", "libexec", "opt", "Cellar",
                                         "Frameworks", "share", "include" })
            await Assert.That(roots).Contains(Path.Combine("/opt/hb", expected));
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
    /// vendor that would not start.</summary>
    [Test]
    public async Task Package_installed_crypto_config_is_granted_by_name() {
        var roots = Resolve("/opt/hb/lib/node_modules/@vendor/tool/loader.js", "/opt/hb");

        await Assert.That(roots).Contains("/opt/hb/etc/openssl@3");
        await Assert.That(roots).Contains("/opt/hb/etc/ca-certificates");
    }

    [Test]
    public async Task The_binarys_own_package_directory_is_granted() {
        var roots = Resolve("/opt/hb/lib/node_modules/@vendor/tool/loader.js", "/opt/hb");

        await Assert.That(roots).Contains("/opt/hb/lib/node_modules/@vendor/tool");
    }

    /// <summary>A prefix is recognized by holding both <c>bin</c> and <c>lib</c>, which is what makes
    /// this neutral across install methods — Homebrew, <c>/usr/local</c>, an nvm/volta node root, or a
    /// self-contained vendor directory — without any of them being named in the code.</summary>
    [Test]
    public async Task A_prefix_is_discovered_by_shape_not_by_name() {
        var roots = Resolve("/home/dev/.volta/tools/image/node/22/lib/node_modules/@vendor/tool/loader.js",
                            "/home/dev/.volta/tools/image/node/22");

        await Assert.That(roots).Contains("/home/dev/.volta/tools/image/node/22/bin");
        await Assert.That(roots).Contains("/home/dev/.volta/tools/image/node/22/lib");
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
        var roots = BorrowedReviewRuntimeRoots.Resolve(binary, _ => true);

        // Route 1: the root as a package directory.
        await Assert.That(roots).DoesNotContain("/");
        await Assert.That(roots).DoesNotContain(Path.GetPathRoot(Path.GetFullPath(binary))!);
        // Route 2: the root treated as an installation prefix.
        await Assert.That(roots).DoesNotContain("/bin");
        await Assert.That(roots).DoesNotContain("/lib");
        await Assert.That(roots).DoesNotContain("/etc");
        await Assert.That(roots).DoesNotContain("/var");
    }

    /// <summary>The same hole one level down: a real installation under <c>/opt/vendor</c> must still
    /// resolve, so the root exclusion must not be implemented by refusing shallow paths generally.</summary>
    [Test]
    public async Task A_shallow_but_non_root_installation_still_resolves() {
        var roots = BorrowedReviewRuntimeRoots.Resolve(
            "/opt/vendor/libexec/tool",
            path => path.Replace('\\', '/') is "/opt/vendor/bin" or "/opt/vendor/lib"
                                            or "/opt/vendor/libexec");

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
        await Assert.That(BorrowedReviewRuntimeRoots.Resolve(path, _ => true)).IsEmpty();
    }

    [Test]
    public async Task Only_paths_that_exist_are_granted() {
        var roots = BorrowedReviewRuntimeRoots.Resolve(
            "/opt/hb/lib/node_modules/@vendor/tool/loader.js",
            path => path.Replace('\\', '/') is "/opt/hb/bin" or "/opt/hb/lib"
                                            or "/opt/hb/lib/node_modules/@vendor/tool");

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

        var roots    = BorrowedReviewRuntimeRoots.Resolve(binary);
        var resolved = new FileInfo(binary).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? binary;

        await Assert.That(roots.Any(root =>
            resolved.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                StringComparison.Ordinal))).IsTrue()
            .Because($"no resolved runtime root covers the real program at {resolved}");
        await Assert.That(roots).DoesNotContain("/opt/homebrew/var");
        await Assert.That(roots).DoesNotContain("/opt/homebrew/etc");
    }
}
