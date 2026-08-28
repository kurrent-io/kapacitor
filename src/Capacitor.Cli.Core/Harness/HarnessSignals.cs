namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// What a harness can say about itself on this machine. A vendor omits what it cannot answer:
/// no binary names means no PATH probe (Cursor ships no CLI), a null predicate means the question
/// has no answer for that vendor rather than a false one — Claude and Codex have no on-disk install
/// marker, because the directories they would be probed through exist on machines that never ran
/// them.
///
/// <para>The predicates are lazy because the callers differ in what they may spend: the SessionStart
/// nudge runs on a latency budget and asks one question, while <c>kcap status</c> asks all of them.
/// </para>
/// </summary>
public readonly record struct HarnessSignals {
    public HarnessSignals() { }

    /// <summary>Command names that mean "installed" when found on PATH. Empty = no PATH probe.</summary>
    public IReadOnlyList<string> Binaries { get; init; } = [];

    /// <summary>Whether the vendor's own state exists on disk. Null = no marker worth probing.</summary>
    public Func<bool>? Installed { get; init; }

    /// <summary>Whether kcap's hook or extension is registered with the vendor.</summary>
    public Func<bool>? Wired { get; init; }

    /// <summary>Asks the marker question now. A vendor with no marker reads as not installed; the
    /// PATH probe is the other half of that answer.</summary>
    public bool IsInstalled => Installed?.Invoke() ?? false;

    /// <summary>Asks the wiring question now. A vendor that cannot answer reads as not wired.</summary>
    public bool IsWired => Wired?.Invoke() ?? false;
}
