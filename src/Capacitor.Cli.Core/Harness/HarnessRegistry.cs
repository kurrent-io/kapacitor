using System.Collections;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// The harnesses this process can see, in setup and display order, and the search path their
/// binaries are looked for on. The one place shared code names a vendor: adding one is a directory
/// plus a line here, and every consumer that treats vendors alike keeps iterating.
///
/// <para>Built once at an entry point and passed down, so two consumers cannot resolve one vendor's
/// root differently — and Antigravity is composed from the Gemini instance rather than from the
/// environment, so the root they share is read once.</para>
///
/// <para><b>No answer is cached.</b> Each detection question stats the disk when asked, so a
/// long-lived holder — the daemon keeps one for its lifetime — sees a vendor installed after it was
/// built. A snapshot is the caller's to take, where it needs one. What IS fixed at build time is
/// where to look: the search path and the vendor roots are read from the environment once, so a
/// holder does not see those variables change under it.</para>
/// </summary>
public sealed class HarnessRegistry : IReadOnlyList<IHarness> {
    readonly IReadOnlyList<IHarness> _harnesses;
    readonly BinaryProbe             _binaries;

    HarnessRegistry(IReadOnlyList<IHarness> harnesses, BinaryProbe binaries) {
        _harnesses = harnesses;
        _binaries  = binaries;
    }

    /// <summary>Every harness's identity, reached through each type's own statics rather than an
    /// instance — the answer for a caller with no home to build one from.</summary>
    public static IReadOnlyList<HarnessIdentity> Identities { get; } = [
        IdentityOf<ClaudeHarness>(),
        IdentityOf<CodexHarness>(),
        IdentityOf<CursorHarness>(),
        IdentityOf<CopilotHarness>(),
        IdentityOf<GeminiHarness>(),
        IdentityOf<KiroHarness>(),
        IdentityOf<PiHarness>(),
        IdentityOf<OpenCodeHarness>(),
        IdentityOf<AntigravityHarness>(),
    ];

    static HarnessIdentity IdentityOf<TSelf>() where TSelf : IHarness<TSelf> => new(TSelf.Id, TSelf.Label);

    /// <summary>Every vendor as this machine's environment resolves it, searched over this process's
    /// own PATH.</summary>
    public static HarnessRegistry FromEnvironment(UserHome home) =>
        FromEnvironment(home, BinaryProbe.FromEnvironment());

    /// <summary>Every vendor as this machine's environment resolves it, searched over
    /// <paramref name="binaries"/> — the composition root's own DI singleton, so this registry and
    /// every other consumer of that singleton search the same path.</summary>
    public static HarnessRegistry FromEnvironment(UserHome home, BinaryProbe binaries) {
        var gemini = GeminiHarness.FromEnvironment(home);

        return new([
            ClaudeHarness.FromEnvironment(home),
            CodexHarness.FromEnvironment(home),
            CursorHarness.FromEnvironment(home),
            CopilotHarness.FromEnvironment(home),
            gemini,
            KiroHarness.FromEnvironment(home),
            PiHarness.FromEnvironment(home),
            OpenCodeHarness.FromEnvironment(home),
            AntigravityHarness.Over(gemini),
        ], binaries);
    }

    /// <summary>Over harnesses resolved elsewhere — a test's, or a subset.</summary>
    public static HarnessRegistry Over(BinaryProbe binaries, params IHarness[] harnesses) =>
        new(harnesses, binaries);

    /// <summary>The same vendors, looked for on another search path. What a GUI launch needs: it
    /// inherits only the launcher's PATH, and spawns through the login shell's.</summary>
    public HarnessRegistry Searching(BinaryProbe binaries) => new(_harnesses, binaries);

    /// <summary>What this label says, for a caller holding an id rather than a harness.</summary>
    public static string LabelOf(HarnessId id) => Identities.First(i => i.Id == id).Label;

    /// <summary>The harness that id names. A registry missing one is a bug, not an input error —
    /// <see cref="ById"/> is what takes an id from outside this process.</summary>
    public IHarness this[HarnessId id] =>
        ById(id) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No such harness in this registry.");

    /// <summary>The one entry whose id matches, or null when the caller was handed an id from
    /// outside this process (a server payload, a flag).</summary>
    public IHarness? ById(HarnessId id) => _harnesses.FirstOrDefault(h => h.Id == id);

    /// <summary>One vendor's own harness, for code that reads that vendor's files and needs its
    /// typed layout.</summary>
    public TSelf Of<TSelf>() where TSelf : IHarness<TSelf> => (TSelf)this[TSelf.Id];

    /// <summary>What a PATH probe and this vendor's own marker say about it, asked now. The two
    /// signals stay apart: the first-run screen names the one it saw.</summary>
    public DetectedAgent Detect(HarnessId id) =>
        ById(id) is { } harness
            ? new DetectedAgent(
                BinaryFound:        harness.Signals.Binaries.Any(_binaries.Finds),
                InstallSignalFound: harness.Signals.IsInstalled)
            // An id this registry never carried — a stale flag, a server payload — reads as absent
            // rather than throwing: neither must take a command down.
            : DetectedAgent.None;

    /// <summary>Whether either signal found this vendor.</summary>
    public bool Detected(HarnessId id) => Detect(id).Detected;

    /// <summary>Full path to the first of <paramref name="id"/>'s declared
    /// <see cref="HarnessSignals.Binaries"/> that resolves on this registry's search path — so a
    /// caller resolving Antigravity does not have to know it answers to two names. Null when none
    /// resolve, when the vendor declares no binaries (Cursor ships no CLI), or when this registry
    /// does not carry <paramref name="id"/>, matching <see cref="Detect"/>.</summary>
    public string? ResolveExecutable(HarnessId id) =>
        ById(id)?.Signals.Binaries.Select(_binaries.Resolve).FirstOrDefault(path => path is not null);

    public int      Count           => _harnesses.Count;
    public IHarness this[int index] => _harnesses[index];

    public IEnumerator<IHarness> GetEnumerator() => _harnesses.GetEnumerator();
    IEnumerator IEnumerable.      GetEnumerator() => GetEnumerator();
}
