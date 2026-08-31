using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.Harness.Kiro;
using Capacitor.Cli.Harness.OpenCode;
using Capacitor.Cli.Harness.Pi;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Pins <see cref="IImportSource.AttachesChildContentOnReplay"/> for every source. That flag is
/// what the orchestrator's <c>--private</c> pass uses to decide which routed sessions get
/// privatized independently of their <see cref="ImportOutcome"/>, so a wrong value is a silent
/// privacy regression rather than a visible failure: too narrow and a replay that attached child
/// content to an already-public session stays public; too wide and a plain replay that posted
/// nothing gets a needless visibility PUT.
///
/// <para>
/// The behavioural end of this contract lives in
/// <c>Capacitor.Cli.Tests.Integration.RoutedReplayPrivatizeTests</c> (and, for Cursor,
/// <c>CursorPrivatizeLifecycleFailureTests</c>); this table is the cheap per-source guard that
/// makes a new or changed source declare its answer deliberately.
/// </para>
/// </summary>
public class ReplayChildContentCapabilityTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>
    /// The three sources whose child-import pass is reachable when the root is already fully
    /// ingested — the shape that can add content to a session that already exists.
    /// </summary>
    [Test]
    [Arguments("cursor")]
    [Arguments("antigravity")]
    [Arguments("gemini")]
    public async Task sources_whose_replay_can_attach_child_content_declare_the_capability(string vendor) {
        await Assert.That(MakeSource(vendor).AttachesChildContentOnReplay).IsTrue();
    }

    /// <summary>
    /// Claude/Codex are chain-based and never reach the routed loop at all. Copilot/Kiro/Pi have
    /// no child import. OpenCode does import descendants, but its ImportSessionAsync early-returns
    /// Skipped for AlreadyLoaded before posting anything — see
    /// <c>ImportVisibilityTests.OpenCode_already_loaded_session_is_skipped_before_any_session_start</c>,
    /// which pins that early return; this entry must flip if it ever gains a repair pass.
    /// </summary>
    [Test]
    [Arguments("claude")]
    [Arguments("codex")]
    [Arguments("copilot")]
    [Arguments("kiro")]
    [Arguments("pi")]
    [Arguments("opencode")]
    [Arguments("dsh")]
    public async Task sources_whose_replay_cannot_attach_child_content_do_not_declare_the_capability(string vendor) {
        await Assert.That(MakeSource(vendor).AttachesChildContentOnReplay).IsFalse();
    }

    /// <summary>
    /// Guards the table above against silently drifting out of sync with the real source list: a
    /// newly-added <see cref="IImportSource"/> has to be classified here rather than quietly
    /// defaulting to "not captured".
    /// </summary>
    [Test]
    public async Task every_import_source_is_covered_by_this_table() {
        var declared = new[] {
            HarnessId.Cursor, HarnessId.Antigravity, HarnessId.Gemini, HarnessId.Claude, HarnessId.Codex,
            HarnessId.Copilot, HarnessId.Kiro, HarnessId.Pi, HarnessId.OpenCode, HarnessId.Dsh,
        };

        var actual = typeof(IImportSource).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IImportSource).IsAssignableFrom(t))
            .Select(t => MakeSource(VendorOf(t)).Vendor)
            .ToHashSet();

        await Assert.That(actual.Except(declared)).IsEmpty();
        await Assert.That(declared.Except(actual)).IsEmpty();
    }

    static string VendorOf(Type t) => t.Name.Replace("ImportSource", "").ToLowerInvariant();

    // Constructed with throwaway paths: only the capability/vendor properties are read, and no
    // source touches disk in its constructor.
    IImportSource MakeSource(string vendor) {
        using var tmp = new TempDir();
        var scratch = tmp.PathTo("capability-probe");

        return vendor switch {
            "claude"      => new ClaudeImportSource(Config.Root, scratch),
            "codex"       => new CodexImportSource(Config.Root, scratch),
            "copilot"     => new CopilotImportSource(Config.Root, CopilotHarness.FromEnvironment(Home).Paths),
            "cursor"      => new CursorImportSource(Config.Root, scratch, scratch),
            "gemini"      => new GeminiImportSource(scratch),
            "kiro"        => new KiroImportSource(Config.Root, KiroHarness.FromEnvironment(Home).Paths.SessionsDir),
            "pi"          => new PiImportSource(Config.Root, PiHarness.FromEnvironment(Home).Paths.SessionsDir),
            "opencode"    => new OpenCodeImportSource(Path.Combine(scratch, "db"), Path.Combine(scratch, "ledger")),
            "antigravity" => new AntigravityImportSource(new(new(scratch), "")),
            "dsh"         => new DshImportSource(Config.Root, scratch),
            _             => throw new ArgumentOutOfRangeException(nameof(vendor), vendor, "unclassified import source"),
        };
    }
}
