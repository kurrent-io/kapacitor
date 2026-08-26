using System.Runtime.CompilerServices;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// Where the join key is minted, and — because the bugs it guards are silent — that the
/// once-per-device <c>cli_first_run</c> event actually carries it.
///
/// <para>The key is minted in <c>CliTelemetry.Initialize</c>, gated to the two interactive auth
/// commands (<c>setup</c>/<c>login</c>), BEFORE <c>NoticeAndFirstRun</c> captures <c>cli_first_run</c>.
/// Two placement bugs are cheap to reintroduce and produce no error at all. Mint AFTER
/// <c>cli_first_run</c> and that once-ever event ships without the key, with no later run able to
/// repair it. Mint UNCONDITIONALLY and every <c>recap</c>/<c>import</c> carries a key that correlates
/// nothing, redefining a per-auth-run id as a process id.</para>
///
/// <para>Pinned behaviourally, not by source shape: the mint no longer opens a command handler — the
/// form the earlier guard scanned for — so "is it the first code" says nothing now. The behavioural
/// tests capture <c>cli_first_run</c> through a <c>TestSink</c> (no network, no disk), which is the
/// only form that proves the ordering rather than a proxy for it. A source enumeration still backs
/// them up, because a mint added to a second file would slip past a test that only drives
/// <c>Initialize</c>.</para>
/// </summary>
[NotInParallel([
    nameof(CliTelemetry) + "." + nameof(CliTelemetry.TestSink),
])]
public class SetupJoinMintPlacementGuardTests : IDisposable {
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    // CliTelemetry AND SetupJoin both hold process-global static state; reset both so a test here
    // starts from pristine state rather than inheriting a prior Enabled=false or an already-minted key.
    [Before(Test)]
    public void ResetStatics() {
        CliTelemetry.Reset();
        SetupJoin.Reset();
    }

    // The regression this whole feature's contract turns on: cli_first_run is captured once per
    // device, inside Initialize, and for an auth run it must carry the join key. Minting after
    // NoticeAndFirstRun leaves it without one, and being once-ever, no later run repairs it.
    [Test]
    [Arguments("setup")]
    [Arguments("login")]
    public async Task cli_first_run_carries_the_join_key_for_an_auth_command(string command) {
        var config = new ConfigRoot(_tmp.Path);
        var sink   = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize(command, null, loggedIn: false, config);
        TelemetryTestGuards.AssertEnabled(command, config);

        await Assert.That(SetupJoin.Current).IsNotNull().Because("an auth command mints the key");

        var firstRun = sink.SingleOrDefault(e => e.Name == "cli_first_run");
        await Assert.That(firstRun).IsNotNull()
            .Because("a fresh device's first auth command emits cli_first_run");
        await Assert.That(firstRun!.Properties[SetupJoin.PropertyName]?.GetValue<string>())
            .IsEqualTo(SetupJoin.Current)
            .Because("cli_first_run must carry the minted key, which requires the mint to precede NoticeAndFirstRun");
    }

    // recap is reportable, so a fresh device still emits cli_first_run for it — but it has no auth run
    // to correlate, so it must mint nothing. An unconditional mint in Initialize is exactly what this
    // catches: it would set Current and stamp join_id onto recap/import events.
    [Test]
    public async Task A_non_auth_command_mints_no_join_key() {
        var config = new ConfigRoot(_tmp.Path);
        var sink   = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("recap", null, loggedIn: false, config);
        TelemetryTestGuards.AssertEnabled("recap", config);

        await Assert.That(SetupJoin.Current).IsNull().Because("a non-auth command must not mint the key");

        var firstRun = sink.SingleOrDefault(e => e.Name == "cli_first_run");
        await Assert.That(firstRun).IsNotNull().Because("recap is reportable, so a fresh device still emits cli_first_run");
        await Assert.That(firstRun!.Properties.ContainsKey(SetupJoin.PropertyName)).IsFalse()
            .Because("no auth run means no join_id on the event");
    }

    // === Source backstop: the mint lives in exactly one file. ===

    // A mint in a second file is invisible to the behavioural tests above — they only drive
    // Initialize. Enumerating src/ catches a mint re-added to a command handler or buried in a lane,
    // the two homes the earlier design used and the two this one deliberately moved away from.
    [Test]
    public async Task Only_CliTelemetry_mints_the_join_key() {
        var minting = FindMintingFiles(SrcRoot()).Order().ToArray();

        await Assert.That(minting).IsEquivalentTo(new[] { "CliTelemetry.cs" })
            .Because("the join key is minted once, in CliTelemetry.Initialize; a mint anywhere else is "
                   + "either too late (past cli_first_run) or lane-dependent");
    }

    // Proves the enumeration detects a mint and ignores a commented one, against a synthetic fixture.
    [Test]
    public async Task Scanner_finds_a_real_mint_and_ignores_a_commented_one() {
        using var tmp = new TempDir();
        tmp.CreateFile("Real.cs", [
            "namespace Fixture;",
            "static class Real { static void Go() { SetupJoin.Mint(); } }",
        ]);
        tmp.CreateFile("Documented.cs", [
            "namespace Fixture;",
            "// never SetupJoin.Mint() from here",
            "static class Documented { }",
        ]);

        await Assert.That(FindMintingFiles(tmp.Path)).IsEquivalentTo(new[] { "Real.cs" });
    }

    const string Mint = "SetupJoin.Mint()";

    static string SrcRoot() => Path.Combine(RepoRoot(), "src");

    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        if (dir is null)
            throw new InvalidOperationException($"Could not locate repo root (Capacitor.slnx) walking up from {here}");

        return dir;
    }

    // Files under root that mint in code. bin/obj are excluded — they sit inside src/ and hold
    // generated sources. A mint on a // line is documentation, not code.
    static List<string> FindMintingFiles(string root) {
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                   .Any(segment => segment is "bin" or "obj"))) {
            var source = File.ReadAllText(file);
            var from   = 0;

            while ((from = source.IndexOf(Mint, from, StringComparison.Ordinal)) >= 0) {
                var lineStart = source.LastIndexOf('\n', from) + 1;
                if (!source[lineStart..from].Contains("//", StringComparison.Ordinal)) {
                    found.Add(Path.GetFileName(file));
                    break;
                }
                from += Mint.Length;
            }
        }

        return found;
    }
}
