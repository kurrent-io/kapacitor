using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// A harness whose answers are stated outright, for the mapping code — the nudge predicate, the
/// inventory, the first-run report — which needs an id and two answers, never a vendor's files.
/// </summary>
public sealed record TestHarness(HarnessId Id, string Label, HarnessSignals Signals) : IHarness;

public static class TestHarnesses {
    /// <summary>Every harness this build knows, in registry order, detected and wired only where
    /// named. Detection comes off the marker signal, so nothing has to be staged on PATH.</summary>
    public static HarnessRegistry All(HarnessId[]? detected = null, HarnessId[]? wired = null) =>
        HarnessRegistry.Over(
            BinaryProbe.Searching(null),
            [.. HarnessRegistry.Identities.Select(i =>
                Of(i.Id, detected?.Contains(i.Id) ?? false, wired?.Contains(i.Id) ?? false))]);

    /// <summary>A registry over exactly these harnesses, searching <paramref name="binaries"/>.</summary>
    public static HarnessRegistry Over(BinaryProbe binaries, params IHarness[] harnesses) =>
        HarnessRegistry.Over(binaries, harnesses);

    /// <summary>One harness that answers from the search path alone — no marker, whatever names it
    /// declares.</summary>
    public static TestHarness Probing(HarnessId id, params string[] binaries) =>
        new(id, HarnessRegistry.LabelOf(id), new HarnessSignals { Binaries = binaries });

    /// <summary>One harness, both its answers fixed.</summary>
    public static TestHarness Of(HarnessId id, bool detected = false, bool wired = false) =>
        new(id, HarnessRegistry.LabelOf(id), new HarnessSignals {
            Installed = () => detected,
            Wired     = () => wired,
        });

}
