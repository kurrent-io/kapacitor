using Capacitor.App.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Regression coverage for a Bug found in review: Notify pushes into a raw Rx Subject from
/// concurrent background Task.Run bodies (AgentActionService's per-agent stops, pause ops) — Rx's
/// grammar requires OnNext calls to a single Subject be serialized, which a bare Subject does not
/// do on its own. AppNotifier.Notify now holds one lock around both the Subject push and the
/// Console.Error write. A genuine concurrency race is inherently non-deterministic and not
/// asserted here — this instead pins the simpler, deterministic invariant that ordinary sequential
/// calls still deliver in the same relative order to both channels.
public class AppNotifierTests {
    // Swaps the process-global Console.Error — bare NotInParallel (not just a group key) is
    // required, same reasoning as ImportVisibilityTests' Console-redirecting tests: a group key
    // alone would not stop a DIFFERENT group's Console-redirecting test from racing on the same
    // process-global state.
    [Test, NotInParallel]
    public async Task Two_sequential_notifies_deliver_in_order_to_both_channels() {
        var notifier = new AppNotifier();
        var received = new List<string>();
        using var subscription = notifier.Messages.Subscribe(received.Add);

        using var capture = ConsoleOutput.StartErrorCapture();
        notifier.Notify("first");
        notifier.Notify("second");

        await Assert.That(received).IsEquivalentTo(["first", "second"], CollectionOrdering.Matching);

        var stderrLines = capture.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(stderrLines).IsEquivalentTo(["kcap: first", "kcap: second"], CollectionOrdering.Matching);
    }
}
