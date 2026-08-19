using System.Runtime.CompilerServices;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// An architectural guard on WHERE the join key is minted, because two placement bugs are cheap
/// to reintroduce and produce no error at all.
///
/// <para>Mint after the funnel's entry event and <c>cli_setup_started</c> — the denominator every
/// later percentage is measured against — goes out without the key. Mint inside a lane, say the
/// loopback one, and the device-code, headless and no-browser lanes never reach it, so every
/// automated, CI and remote-shell sign-in carries no key at all. Neither failure throws, logs, or
/// turns a test red.</para>
///
/// <para>Both are prevented structurally by minting as the FIRST statement of the command handler,
/// before any funnel event and before the flag parsing that chooses a lane. This guard is what
/// keeps it there. It reads source rather than running the handlers because the behavioural form
/// would perform real filesystem and network work.</para>
///
/// <para><b>Scope: every <c>.cs</c> file under <c>src/</c>.</b> The mint used to live in
/// <c>Program.cs</c>'s <c>case "login"</c> arm and this guard asserted exactly that; the onboarding
/// wizard reduced that arm to a single delegating line and moved the mint into
/// <c>LoginCommand</c>, where a location-specific assertion could only have gone stale or gone
/// green for the wrong reason. So the mint sites are DISCOVERED and then checked, and the set of
/// files allowed to hold one is asserted — which is also what catches a mint added inside a lane.
/// </para>
/// </summary>
public class SetupJoinMintPlacementGuardTests {
    const string Mint = "SetupJoin.Mint()";

    /// <summary>
    /// The command entry points that begin one interactive auth run, and nothing else. A mint
    /// anywhere else is either too late (past a funnel event) or too deep (inside one lane).
    /// </summary>
    static readonly string[] EntryPoints = ["SetupCommand.cs", "LoginCommand.cs"];

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

    static string SrcRoot() => Path.Combine(RepoRoot(), "src");

    /// <summary>Every file under <paramref name="srcRoot"/> that mints, with its source. Occurrences
    /// on a <c>//</c> line are documentation, not a mint.</summary>
    internal static List<(string File, string Source)> FindMintingFiles(string srcRoot) {
        var found = new List<(string, string)>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)) {
            var source = File.ReadAllText(file);
            if (MintIndexes(source).Count > 0) found.Add((Path.GetFileName(file), source));
        }

        return found;
    }

    /// <summary>Indexes of every mint in code — comment mentions excluded.</summary>
    static List<int> MintIndexes(string source) {
        var found = new List<int>();
        var from  = 0;

        while ((from = source.IndexOf(Mint, from, StringComparison.Ordinal)) >= 0) {
            var lineStart = source.LastIndexOf('\n', from) + 1;

            if (!source[lineStart..from].Contains("//", StringComparison.Ordinal)) found.Add(from);
            from += Mint.Length;
        }

        return found;
    }

    /// <summary>
    /// True when the mint is the first statement of the <c>HandleAsync</c> that encloses it: no
    /// other statement-terminating <c>;</c> stands between the handler's opening brace and the mint.
    /// <para>An offset rather than a line-count budget, because the correct code's mint sits under a
    /// multi-line comment explaining why it is there, and a budget makes that comment load-bearing.
    /// </para>
    /// </summary>
    internal static bool MintsAsTheFirstStatementOfItsHandler(string source) {
        foreach (var mint in MintIndexes(source)) {
            var signature = source.LastIndexOf("HandleAsync(", mint, StringComparison.Ordinal);
            if (signature < 0) return false;

            var body = source.IndexOf('{', signature);
            if (body < 0 || body > mint) return false;

            if (WithoutLineComments(source[(body + 1)..mint]).Contains(';')) return false;
        }

        return true;
    }

    // A `;` inside an explanatory comment is not a statement.
    static string WithoutLineComments(string text) =>
        string.Join('\n', text.Split('\n').Select(line => {
            var slashes = line.IndexOf("//", StringComparison.Ordinal);

            return slashes < 0 ? line : line[..slashes];
        }));

    // === The real guard ===

    // A mint deeper than a command handler is the lane-dependence bug: whichever lane holds it, the
    // others carry no key. Enumerating src/ is the only form that catches one added to a file that
    // did not exist when this guard was written — which is exactly what happened once already.
    [Test]
    public async Task Only_the_command_entry_points_mint_the_join_key() {
        var minting = FindMintingFiles(SrcRoot()).Select(f => f.File).Order().ToArray();

        await Assert.That(minting).IsEquivalentTo(EntryPoints.Order().ToArray());
    }

    [Test]
    public async Task Every_mint_is_the_first_statement_of_its_handler() {
        foreach (var (file, source) in FindMintingFiles(SrcRoot())) {
            await Assert.That(MintsAsTheFirstStatementOfItsHandler(source)).IsTrue()
                .Because($"{file} mints after another statement, so a lane or a funnel event can precede it");
        }
    }

    // cli_setup_started is the funnel's entry event — the denominator every later percentage is
    // measured against — so a key that arrives after it cannot join the run to its web session.
    [Test]
    public async Task Setup_mints_the_join_key_before_the_funnel_entry_event() {
        var source = await File.ReadAllTextAsync(
            Path.Combine(SrcRoot(), "Capacitor.Cli", "Commands", "SetupCommand.cs"));

        var mint    = source.IndexOf(Mint, StringComparison.Ordinal);
        var started = source.IndexOf("SetupFunnel.Started(", StringComparison.Ordinal);

        await Assert.That(mint).IsGreaterThan(-1);
        await Assert.That(started).IsGreaterThan(-1);
        await Assert.That(mint).IsLessThan(started);
    }

    // The key means "one interactive auth run". Minting it in CliTelemetry.Initialize would put it
    // on `kcap recap` and `kcap import` too, redefining it as a process id and attaching it to
    // events that have nothing to correlate. Covered by the enumeration above as well; named here
    // because it is the specific redefinition most likely to look like a simplification.
    [Test]
    public async Task The_key_is_not_minted_for_every_command_in_Initialize() {
        var source = await File.ReadAllTextAsync(
            Path.Combine(SrcRoot(), "Capacitor.Cli.Core", "Telemetry", "CliTelemetry.cs"));

        await Assert.That(source).DoesNotContain(Mint);
    }

    // === Scanner self-tests: prove the detector detects, against a synthetic fixture. ===

    [Test]
    public async Task Scanner_accepts_a_mint_that_opens_the_handler_under_its_comment() {
        using var tmp = new TempDir();

        tmp.CreateFile("Good.cs", [
            "namespace Fixture;",
            "static class Good {",
            "    public static async Task<int> HandleAsync(string[] args) {",
            "        // Before any lane is chosen; note the semicolon in this sentence; it is prose.",
            "        SetupJoin.Mint();",
            "        var forceDevice = args.Contains(\"--device\");",
            "    }",
            "}",
        ]);

        var minting = FindMintingFiles(tmp.Path);

        await Assert.That(minting.Count).IsEqualTo(1);
        await Assert.That(MintsAsTheFirstStatementOfItsHandler(minting[0].Source)).IsTrue();
    }

    [Test]
    public async Task Scanner_flags_a_mint_that_follows_another_statement() {
        using var tmp = new TempDir();

        tmp.CreateFile("Late.cs", [
            "namespace Fixture;",
            "static class Late {",
            "    public static async Task<int> HandleAsync(string[] args) {",
            "        SetupFunnel.Started(source: \"cli\");",
            "        SetupJoin.Mint();",
            "    }",
            "}",
        ]);

        await Assert.That(MintsAsTheFirstStatementOfItsHandler(FindMintingFiles(tmp.Path)[0].Source)).IsFalse();
    }

    // The lane-dependence bug in its natural habitat: a mint inside the loopback path, where a
    // device-code or headless run never reaches it.
    [Test]
    public async Task Scanner_flags_a_mint_that_is_not_in_a_handler_at_all() {
        using var tmp = new TempDir();

        tmp.CreateFile("Lane.cs", [
            "namespace Fixture;",
            "static class Lane {",
            "    static void OpenLoopback() {",
            "        SetupJoin.Mint();",
            "    }",
            "}",
        ]);

        var minting = FindMintingFiles(tmp.Path);

        await Assert.That(minting.Count).IsEqualTo(1);
        await Assert.That(minting[0].File).IsEqualTo("Lane.cs");
        await Assert.That(MintsAsTheFirstStatementOfItsHandler(minting[0].Source)).IsFalse();
    }

    [Test]
    public async Task Scanner_ignores_a_mint_mentioned_in_a_comment() {
        using var tmp = new TempDir();

        tmp.CreateFile("Documented.cs", [
            "namespace Fixture;",
            "/// <summary>Callers begin with SetupJoin.Mint() at handler entry.</summary>",
            "static class Documented {",
            "    // never SetupJoin.Mint() from inside a lane",
            "}",
        ]);

        await Assert.That(FindMintingFiles(tmp.Path)).IsEmpty();
    }
}
