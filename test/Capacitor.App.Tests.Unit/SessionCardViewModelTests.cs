using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class SessionCardViewModelTests {
    /// A finished turn is the one status the daemon's vocabulary does not spell: the card names
    /// it, and keeps the running dot because the process is live.
    [Test]
    public async Task Awaiting_input_reads_as_waiting_for_input_on_the_running_dot() {
        var waiting  = new SessionCardViewModel(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo") with { AwaitingInput = true });
        var working  = new SessionCardViewModel(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo"));
        var reviewer = new SessionCardViewModel(Agent("r1", "codex", hasTerminal: false, repoPath: "/repo", kind: "review-flow") with { AwaitingInput = true });

        await Assert.That(waiting.StatusText).IsEqualTo("Waiting for input");
        await Assert.That(waiting.StatusDot).IsSameReferenceAs(SessionStatusDots.For("Running"));
        await Assert.That(working.StatusText).IsEqualTo("Running");
        // A flow participant between rounds waits on the flow, which the user cannot answer.
        await Assert.That(reviewer.StatusText).IsEqualTo("Running");
    }
}
