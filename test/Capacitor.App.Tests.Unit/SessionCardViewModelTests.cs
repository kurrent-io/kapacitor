using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class SessionCardViewModelTests {
    static AgentStatusDto Dto(string? title) => new(
        "a1", "agent", "claude", "/repos/kcap-cli", "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null, Title: title);

    [Test]
    public async Task The_card_leads_with_the_session_title_and_keeps_repo_and_vendor_below() {
        var vm = new SessionCardViewModel(Dto("Fix the login flow"));

        await Assert.That(vm.Title).IsEqualTo("Fix the login flow");
        await Assert.That(vm.Sub).IsEqualTo("/repos/kcap-cli · claude");
    }

    [Test]
    public async Task Without_a_title_the_card_keeps_its_repo_vendor_label() {
        var vm = new SessionCardViewModel(Dto(null));

        await Assert.That(vm.Title).IsEqualTo("kcap-cli · claude");
        await Assert.That(vm.Sub).IsEqualTo("/repos/kcap-cli");
    }

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
