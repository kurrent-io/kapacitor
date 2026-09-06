using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

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
}
