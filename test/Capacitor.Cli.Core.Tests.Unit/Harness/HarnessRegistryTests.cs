using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

/// <summary>
/// The registry is the one place shared code names a vendor, so these pin what a reader of a
/// single entry cannot check for itself: that every harness is present exactly once, and that each
/// reports its own identity rather than a neighbour's.
/// </summary>
public class HarnessRegistryTests {
    [TempHome] public required TempHome Home { get; init; }

    // Bare: FromEnvironment reads every vendor override variable.
    [Test, NotInParallel]
    public async Task Every_harness_is_registered_exactly_once() {
        var ids = HarnessRegistry.FromEnvironment(Home).Select(h => h.Id).ToList();

        // Both directions: a missing vendor is a harness nothing can reach, and a duplicate is the
        // copy-paste this shape invites — a module declaring IHarness<SomeOtherVendor> compiles and
        // then reports that vendor's identity.
        await Assert.That(ids).IsEquivalentTo(Enum.GetValues<HarnessId>());
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);
    }

    [Test, NotInParallel]
    public async Task Every_harness_carries_a_label() {
        var labels = HarnessRegistry.FromEnvironment(Home).Select(h => h.Label).ToList();

        await Assert.That(labels.Any(string.IsNullOrWhiteSpace)).IsFalse();
        await Assert.That(labels.Distinct().Count()).IsEqualTo(labels.Count);
    }

    /// <summary>Each vendor's layout comes from its own factory rather than the registry reading the
    /// override variables itself, so relocating one vendor leaves the others where they were.</summary>
    // Bare: the overrides are inherited by any child a concurrent test spawns.
    [Test, NotInParallel]
    public async Task Each_vendor_is_routed_through_its_own_override() {
        using var relocated = new TempDir();

        using var claude = EnvScope.Exclusive("CLAUDE_CONFIG_DIR", relocated.PathTo("claude"));
        using var codex  = EnvScope.Exclusive("CODEX_HOME", relocated.PathTo("codex"));
        using var gemini = EnvScope.Exclusive("GEMINI_CLI_HOME", relocated.Path);
        using var kiro   = EnvScope.Exclusive("KIRO_HOME", relocated.PathTo("kiro"));

        var harnesses = HarnessRegistry.FromEnvironment(new("/fake/home"));

        await Assert.That(harnesses.Of<ClaudeHarness>().Paths.Home).IsEqualTo(relocated.PathTo("claude"));
        await Assert.That(harnesses.Of<CodexHarness>().Paths.Home).IsEqualTo(relocated.PathTo("codex"));
        await Assert.That(harnesses.Of<GeminiHarness>().Paths.Root).IsEqualTo(relocated.PathTo(".gemini"));
        await Assert.That(harnesses.Of<KiroHarness>().Paths.ConfigRoot).IsEqualTo(relocated.PathTo("kiro"));
        // Untouched variables leave their vendor on the injected home.
        await Assert.That(harnesses.Of<PiHarness>().Paths.Root).IsEqualTo(Path.Combine("/fake/home", ".pi"));
    }

    /// <summary>Antigravity's layout hangs off Gemini's root, and the registry composes it from the
    /// same instance — the reason its module has no <c>FromEnvironment</c> of its own, and why a
    /// relocated Gemini root takes Antigravity with it.</summary>
    [Test, NotInParallel]
    public async Task Antigravity_hangs_off_the_same_gemini_root() {
        using var relocated = new TempDir();

        using var gemini = EnvScope.Exclusive("GEMINI_CLI_HOME", relocated.Path);

        var harnesses = HarnessRegistry.FromEnvironment(new("/fake/home"));

        await Assert.That(harnesses.Of<AntigravityHarness>().Paths.McpConfigJson)
            .StartsWith(harnesses.Of<GeminiHarness>().Paths.Root);
    }

    [Test]
    public async Task Resolves_the_executable_a_vendor_declares() {
        using var bin = new TempDir();
        var       probe = TestBinaries.Searching(bin, "claude");

        var harnesses = TestHarnesses.Over(probe, TestHarnesses.Probing(HarnessId.Claude, "claude"));

        await Assert.That(harnesses.ResolveExecutable(HarnessId.Claude)).IsEqualTo(probe.Resolve("claude"));
    }

    /// Cursor ships no CLI, so it declares no binary names — nothing to resolve regardless of what
    /// is staged on the search path.
    [Test]
    public async Task A_vendor_declaring_no_binaries_yields_null() {
        using var bin = new TempDir();
        var       probe = TestBinaries.Searching(bin, "cursor");

        var harnesses = TestHarnesses.Over(probe, TestHarnesses.Probing(HarnessId.Cursor));

        await Assert.That(harnesses.ResolveExecutable(HarnessId.Cursor)).IsNull();
    }

    /// Mirrors <see cref="HarnessRegistry.Detect"/>: an id this registry never carried reads as
    /// absent rather than throwing.
    [Test]
    public async Task An_id_this_registry_does_not_carry_yields_null() {
        var harnesses = TestHarnesses.Over(
            BinaryProbe.Searching(null), TestHarnesses.Probing(HarnessId.Claude, "claude"));

        await Assert.That(harnesses.ResolveExecutable(HarnessId.Codex)).IsNull();
    }

    /// Antigravity declares its product name and its CLI name; a caller must not have to know to try
    /// both — the registry resolves via whichever one is actually on the search path.
    [Test]
    public async Task Resolves_via_a_later_declared_name_when_an_earlier_one_is_absent() {
        using var bin = new TempDir();
        var       probe = TestBinaries.Searching(bin, "agy");

        var harnesses = TestHarnesses.Over(
            probe, TestHarnesses.Probing(HarnessId.Antigravity, "antigravity", "agy"));

        await Assert.That(harnesses.ResolveExecutable(HarnessId.Antigravity)).IsEqualTo(probe.Resolve("agy"));
    }
}
