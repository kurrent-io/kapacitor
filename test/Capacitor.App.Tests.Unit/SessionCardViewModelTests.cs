using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class SessionCardViewModelTests {
    /// A finished turn is the one status the daemon's vocabulary does not spell: the card names
    /// it, and keeps the running dot because the process is live.
    [Test]
    public async Task Awaiting_input_reads_as_waiting_for_input_on_the_running_dot() {
        var waiting = new SessionCardViewModel(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo") with { AwaitingInput = true });
        var working = new SessionCardViewModel(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo"));

        await Assert.That(waiting.StatusText).IsEqualTo("Waiting for input");
        await Assert.That(waiting.StatusDot).IsSameReferenceAs(SessionStatusDots.For("Running"));
        await Assert.That(working.StatusText).IsEqualTo("Running");
    }
}
