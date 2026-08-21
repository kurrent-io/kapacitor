using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Tests.Unit;

/// Intent only (spec §3 step 2): nothing here reaches the network or writes anything, so these
/// run without the headless session — the step owns no commands and no Rx subscriptions.
public class ConnectStepViewModelTests {
    [Test]
    [Arguments("acme", "https://acme.kcap.ai")]
    [Arguments("  acme  ", "https://acme.kcap.ai")]
    [Arguments("https://acme.kcap.ai/sessions/42", "https://acme.kcap.ai")]
    [Arguments("acme.kcap.ai", "https://acme.kcap.ai")]
    [Arguments("http://localhost:5108", "http://localhost:5108")]
    public async Task Paste_stages_the_normalized_server_and_satisfies_the_step(string typed, string expected) {
        var vm = new ConnectStepViewModel { Choice = ConnectChoice.Paste, ServerInputText = typed };

        await Assert.That(vm.Intent).IsEqualTo(new ConnectIntent.Paste(expected));
        await Assert.That(vm.Satisfied).IsTrue();
        await Assert.That(await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();
        await Assert.That(vm.InputError).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("file:///tmp/nope")]
    [Arguments("ftp://acme.example")]
    public async Task Paste_with_an_unusable_server_blocks_next_with_an_inline_error(string typed) {
        var vm = new ConnectStepViewModel { Choice = ConnectChoice.Paste, ServerInputText = typed };

        await Assert.That(vm.Intent).IsNull();
        await Assert.That(vm.Satisfied).IsFalse();
        await Assert.That(await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsFalse();
        await Assert.That(vm.InputError).IsNotNull();
    }

    [Test]
    public async Task Editing_the_input_clears_a_stale_inline_error() {
        var vm = new ConnectStepViewModel { Choice = ConnectChoice.Paste, ServerInputText = "file:///tmp/nope" };
        await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        vm.ServerInputText = "acme";

        await Assert.That(vm.InputError).IsNull();
    }

    [Test]
    [Arguments(AuthProvider.GitHubApp)]
    [Arguments(AuthProvider.WorkOS)]
    public async Task Discover_stages_the_chosen_provider(string provider) {
        var vm = new ConnectStepViewModel { Choice = ConnectChoice.Discover, DiscoveryProvider = provider };

        await Assert.That(vm.Intent).IsEqualTo(new ConnectIntent.Discover(provider));
        await Assert.That(vm.Satisfied).IsTrue();
        await Assert.That(await vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Create_stages_the_create_intent() {
        var vm = new ConnectStepViewModel { Choice = ConnectChoice.Create };

        await Assert.That(vm.Intent).IsEqualTo(new ConnectIntent.Create());
        await Assert.That(vm.Satisfied).IsTrue();
    }

    [Test]
    public async Task Back_and_skip_are_never_blocked_even_with_an_unusable_input() {
        var vm = new ConnectStepViewModel { Choice = ConnectChoice.Paste, ServerInputText = "file:///tmp/nope" };

        await Assert.That(await vm.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None)).IsTrue();
        await Assert.That(await vm.CanLeaveAsync(WizardNavigation.Skip, CancellationToken.None)).IsTrue();
        await Assert.That(vm.InputError).IsNull(); // no error styling for a step the user is leaving
    }

    [Test]
    public async Task Prefill_switches_to_paste_and_populates_the_input() {
        var vm = new ConnectStepViewModel { Choice = ConnectChoice.Discover };

        vm.Prefill("acme");

        await Assert.That(vm.Choice).IsEqualTo(ConnectChoice.Paste);
        await Assert.That(vm.ServerInputText).IsEqualTo("acme");
        await Assert.That(vm.Intent).IsEqualTo(new ConnectIntent.Paste("https://acme.kcap.ai"));
    }

    [Test]
    public async Task The_step_identifies_itself_as_the_connect_page_and_is_always_applicable() {
        var vm = new ConnectStepViewModel();

        await vm.OnEnterAsync(CancellationToken.None);

        await Assert.That(vm.Id).IsEqualTo(WizardStepId.Connect);
        await Assert.That(vm.Applicable).IsTrue();
        await Assert.That(vm.Title).IsNotEmpty();
    }
}
