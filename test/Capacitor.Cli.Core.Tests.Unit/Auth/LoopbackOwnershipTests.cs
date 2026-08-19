using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Auth;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Who shuts the loopback listener down, and does every construction site wire up the join.
///
/// <para>Construction used to be free to inline as an argument, because <c>using var</c> inside the
/// browser handled teardown. It no longer does — the listener outlives <c>InvokeAsync</c> for the
/// return-hop wait — so an unnamed instance holds the port for the life of the process, and a site
/// that omits the collaborator serves today's dead-end page with the whole feature silently absent.
/// Neither failure throws or turns a test red.</para>
///
/// <para><b>Scope: every <c>.cs</c> file under <c>src/</c>.</b> An earlier version of this guard
/// named the two files that happened to construct one, and went blind the moment the onboarding
/// wizard added a third site in a new file — passing while scanning a file that no longer had a
/// site at all. A guard whose whole purpose is to catch "someone added one of these somewhere"
/// cannot encode today's file layout.</para>
///
/// <para>Guarded at source because the behavioural form would bind a port and launch a browser.
/// <see cref="FindOwnershipViolations"/> takes its root as a parameter so the scanner self-tests
/// below can prove it actually detects, against a synthetic fixture rather than real source.</para>
///
/// <para><b>Two checks, because pattern matching over source can always be out-spelled.</b> The
/// scanner tolerates trivia and qualification between <c>new</c> and the argument list — a literal
/// <c>"new LoopbackBrowser("</c> search missed <c>new LoopbackBrowser /* c */ (…)</c>, a line break
/// before the paren, and a <c>global::</c>-qualified name, all of which compile. Since no such
/// pattern can be proven complete, <see cref="Only_the_known_files_name_the_browser_type_at_all"/>
/// additionally pins the set of files allowed to NAME the type at all: whatever syntax a new lane
/// invents, it has to name it, so that assertion is the one no spelling slips past.</para>
/// </summary>
public class LoopbackOwnershipTests {
    /// <summary>
    /// A construction, tolerating what the compiler tolerates: any whitespace or line break after
    /// <c>new</c>, an optional <c>global::</c> and dotted namespace qualification, and block comments
    /// between the type name and the argument list.
    /// </summary>
    static readonly Regex Construction = new(
        @"new\s+(?:global\s*::\s*)?(?:[A-Za-z_]\w*\s*\.\s*)*LoopbackBrowser\s*(?:/\*.*?\*/\s*)*\(",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>An externally supplied browser that would notice being disposed by the callee.</summary>
    sealed class DisposableFakeBrowser(string query) : IBrowser, IDisposable {
        public bool Disposed { get; private set; }

        public Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default) =>
            Task.FromResult(new BrowserResult { ResultType = BrowserResultType.Success, Response = query });

        public void Dispose() => Disposed = true;
    }

    // Disposing an injected browser would tear down a test's stand-in mid-test — or, later, a
    // caller's shared instance. The state mismatch below makes the flow return null without any
    // network call; what matters is what happened to `fake`.
    [Test]
    public async Task An_injected_browser_is_never_disposed_by_the_callee() {
        var fake = new DisposableFakeBrowser("?code=abc&state=mismatch");

        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            "client-id", "http://127.0.0.1:1/exchange", browser: fake, timeout: TimeSpan.FromSeconds(1));

        await Assert.That(token).IsNull();
        await Assert.That(fake.Disposed).IsFalse();
    }

    /// <summary>Walks up from this file's own compile-time path, so it is independent of the
    /// runner's working directory.</summary>
    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        if (dir is null)
            throw new InvalidOperationException($"Could not locate repo root (Capacitor.slnx) walking up from {here}");

        return dir;
    }

    /// <summary>
    /// Every construction site under <paramref name="srcRoot"/>, as
    /// <c>(file, line, statement, arguments)</c>. Skips occurrences on a <c>//</c> line, which are
    /// documentation rather than code.
    /// <para>The statement is taken back to the nearest <c>;</c> <c>{</c> or <c>}</c> — the ternary
    /// form the two flows use spans lines, with the <c>using</c> declaration above the <c>new</c>,
    /// so a per-line test would reject the correct code and a substring of the same line would
    /// accept an inline argument.</para>
    /// </summary>
    internal static List<(string File, int Line, string Statement, string Arguments)> FindSites(string srcRoot) {
        var sites = new List<(string, int, string, string)>();

        foreach (var file in SourceFiles(srcRoot)) {
            var source = File.ReadAllText(file);

            foreach (var match in Construction.Matches(source).Cast<Match>()) {
                var at        = match.Index;
                var lineStart = source.LastIndexOf('\n', at) + 1;

                if (source[lineStart..at].Contains("//", StringComparison.Ordinal)) continue;

                var start = source.LastIndexOfAny([';', '{', '}'], at) + 1;
                var line  = source[..at].Count(c => c == '\n') + 1;

                // match.Index + match.Length - 1 is the '(' the regex consumed, wherever the trivia
                // put it, so the argument list is read from the real paren rather than a fixed offset.
                sites.Add((Path.GetFileName(file), line, source[start..at],
                           ArgumentsAt(source, match.Index + match.Length - 1)));
            }
        }

        return sites;
    }

    /// <summary>
    /// Hand-written <c>.cs</c> under <paramref name="root"/>. <c>bin</c>/<c>obj</c> are excluded
    /// because they sit INSIDE <c>src/</c>: they hold generated sources, and a generator that ever
    /// emitted a copy of a flow would have the scanner counting the same site twice.
    /// </summary>
    static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          .Any(segment => segment is "bin" or "obj"));

    /// <summary>The argument list, paren-balanced so a nested call inside it doesn't truncate.</summary>
    static string ArgumentsAt(string source, int open) {
        var depth = 0;

        for (var i = open; i < source.Length; i++) {
            if (source[i] == '(') depth++;
            else if (source[i] == ')' && --depth == 0) return source[(open + 1)..i];
        }

        return source[open..];
    }

    /// <summary>
    /// One violation string per site that either leaves nobody to dispose the listener or omits the
    /// join collaborator. A <c>using</c> declaration is the accepted form: it names the instance and
    /// scopes its teardown, and on a nullable local it disposes exactly when the local is the one we
    /// built.
    /// </summary>
    internal static List<string> FindOwnershipViolations(string srcRoot) {
        var violations = new List<string>();

        foreach (var (file, line, statement, arguments) in FindSites(srcRoot)) {
            if (!statement.Contains("using ", StringComparison.Ordinal))
                violations.Add($"{file}:{line}: constructed outside a `using` declaration — {Squash(statement)}");

            if (!arguments.Contains("SetupJoin.Loopback", StringComparison.Ordinal))
                violations.Add($"{file}:{line}: does not pass the join collaborator — {Squash(arguments)}");
        }

        return violations;
    }

    static string Squash(string text) => string.Join(' ', text.Split('\n', StringSplitOptions.TrimEntries)).Trim();

    // === The real guard: scans this repo's actual src/ tree ===

    [Test]
    public async Task Every_construction_site_is_owned_and_passes_the_join() {
        var violations = FindOwnershipViolations(Path.Combine(RepoRoot(), "src"));

        await Assert.That(violations).IsEmpty();
    }

    // A guard that finds nothing to check reports success. The three sites today are the two auth
    // flows plus the wizard's orgless login; a drop means one was removed or renamed, and the count
    // is worth re-deriving by hand rather than silently scanning an empty set.
    [Test]
    public async Task The_scan_actually_reaches_the_construction_sites() {
        var sites = FindSites(Path.Combine(RepoRoot(), "src"));

        await Assert.That(sites.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(sites.Select(s => s.File).Distinct().Count()).IsGreaterThanOrEqualTo(2);
    }

    // === Scanner self-tests: prove the detector detects, and that the accepted forms are accepted,
    // against a synthetic fixture directory rather than real source. ===

    [Test]
    public async Task Scanner_accepts_both_forms_the_real_flows_use() {
        using var tmp = new TempDir();

        tmp.CreateFile("Owned.cs", [
            "namespace Fixture;",
            "static class Owned {",
            "    static void Simple() {",
            "        using var browser = new LoopbackBrowser(progress: progress, join: SetupJoin.Loopback);",
            "    }",
            "    static void NullableTernary(object? injected) {",
            "        using LoopbackBrowser? created =",
            "            injected is null ? new LoopbackBrowser(progress: progress, join: SetupJoin.Loopback) : null;",
            "    }",
            "}",
        ]);

        await Assert.That(FindOwnershipViolations(tmp.Path)).IsEmpty();
        await Assert.That(FindSites(tmp.Path).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Scanner_flags_an_inline_construction_that_nobody_disposes() {
        using var tmp = new TempDir();

        tmp.CreateFile("Leaked.cs", [
            "namespace Fixture;",
            "static class Leaked {",
            "    static void Go() {",
            "        var options = new OidcClientOptions {",
            "            Browser = new LoopbackBrowser(progress: progress, join: SetupJoin.Loopback),",
            "        };",
            "    }",
            "}",
        ]);

        var violations = FindOwnershipViolations(tmp.Path);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("outside a `using` declaration");
    }

    [Test]
    public async Task Scanner_flags_a_site_that_omits_the_join_collaborator() {
        using var tmp = new TempDir();

        tmp.CreateFile("Joinless.cs", [
            "namespace Fixture;",
            "static class Joinless {",
            "    static void Go() {",
            "        using var browser = new LoopbackBrowser(progress: progress);",
            "    }",
            "}",
        ]);

        var violations = FindOwnershipViolations(tmp.Path);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("does not pass the join collaborator");
    }

    // The blindness this guard was rewritten to fix: a fourth site in a file nobody thought to
    // list. Enumeration finds it; a name list would not.
    [Test]
    public async Task Scanner_finds_a_site_in_a_file_it_was_never_told_about() {
        using var tmp = new TempDir();

        tmp.CreateFile("Nested/BrandNewFacade.cs", [
            "namespace Fixture.Nested;",
            "static class BrandNewFacade {",
            "    static void Go() {",
            "        var browser = new LoopbackBrowser(progress: progress);",
            "    }",
            "}",
        ]);

        var violations = FindOwnershipViolations(tmp.Path);

        await Assert.That(violations.Count).IsEqualTo(2);
        await Assert.That(violations.TrueForAll(v => v.Contains("BrandNewFacade.cs:4"))).IsTrue();
    }

    // Trivia between the type name and the argument list. All three compile, and a scanner looking
    // for the literal token "new LoopbackBrowser(" sees none of them — so an unowned, joinless site
    // spelled any of these ways would leave every assertion in this file green.
    [Test]
    [Arguments("        var browser = new LoopbackBrowser /* built here */ (progress: progress);")]
    [Arguments("        var browser = new LoopbackBrowser (progress: progress);")]
    [Arguments("        var browser = new global::Capacitor.Cli.Core.Auth.LoopbackBrowser(progress: progress);")]
    public async Task Scanner_finds_a_construction_however_it_is_spelled(string construction) {
        using var tmp = new TempDir();

        tmp.CreateFile("Sneaky.cs", [
            "namespace Fixture;",
            "static class Sneaky {",
            "    static void Go() {",
            construction,
            "    }",
            "}",
        ]);

        await Assert.That(FindSites(tmp.Path).Count).IsEqualTo(1);
        await Assert.That(FindOwnershipViolations(tmp.Path).Count).IsEqualTo(2);
    }

    // A construction split across lines — the argument list on the line after the type name.
    [Test]
    public async Task Scanner_finds_a_construction_split_across_lines() {
        using var tmp = new TempDir();

        tmp.CreateFile("Wrapped.cs", [
            "namespace Fixture;",
            "static class Wrapped {",
            "    static void Go() {",
            "        var browser = new LoopbackBrowser",
            "            (progress: progress);",
            "    }",
            "}",
        ]);

        await Assert.That(FindSites(tmp.Path).Count).IsEqualTo(1);
        await Assert.That(FindOwnershipViolations(tmp.Path).Count).IsEqualTo(2);
    }

    // The spelling-independent backstop. Whatever syntax a new lane uses, it has to NAME the type,
    // so the set of files allowed to mention it is asserted — that is what catches a construction
    // this file's pattern matching does not anticipate.
    [Test]
    public async Task Only_the_known_files_name_the_browser_type_at_all() {
        var naming = SourceFiles(Path.Combine(RepoRoot(), "src"))
            .Where(f => File.ReadAllText(f).Contains("LoopbackBrowser", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order()
            .ToArray();

        await Assert.That(naming).IsEquivalentTo(BrowserNamingFiles.Order().ToArray())
            .Because("a file newly naming the type may be constructing one in a spelling the scanner "
                   + "does not anticipate — add it here only after checking the construction is owned");
    }

    /// <summary>Files under <c>src/</c> permitted to name <see cref="LoopbackBrowser"/>: the class
    /// itself, the two flows that construct one, and two that only mention it in a comment or a
    /// <c>see cref</c>. Adding a sixth is a deliberate edit here, and that is the point — it is the
    /// one check no construction syntax can slip past.</summary>
    static readonly string[] BrowserNamingFiles = [
        "LoopbackBrowser.cs", "OAuthLoginFlow.cs", "OnboardingFacade.cs", "SetupJoin.cs", "SetupCommand.cs",
    ];

    // A doc comment showing the construction is documentation, not a leak.
    [Test]
    public async Task Scanner_ignores_a_construction_mentioned_in_a_comment() {
        using var tmp = new TempDir();

        tmp.CreateFile("Documented.cs", [
            "namespace Fixture;",
            "/// <summary>Callers write new LoopbackBrowser(join: ...) and dispose it.</summary>",
            "static class Documented {",
            "    // never do var b = new LoopbackBrowser();",
            "}",
        ]);

        await Assert.That(FindSites(tmp.Path)).IsEmpty();
    }

    // Paren-balancing: a nested call inside the argument list must not truncate the arguments and
    // turn a compliant site into a phantom violation.
    [Test]
    public async Task Scanner_reads_the_whole_argument_list_past_a_nested_call() {
        using var tmp = new TempDir();

        tmp.CreateFile("Nested.cs", [
            "namespace Fixture;",
            "static class Nested {",
            "    static void Go() {",
            "        using var browser = new LoopbackBrowser(openBrowser: Resolve(url), join: SetupJoin.Loopback);",
            "    }",
            "}",
        ]);

        await Assert.That(FindOwnershipViolations(tmp.Path)).IsEmpty();
    }
}
