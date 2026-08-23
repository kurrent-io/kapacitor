using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class HomeViewModelTests {
    sealed class RecordingLaunchClient : ILaunchClient {
        public LaunchRequest? Last;
        public LaunchOutcome Next = new(true, "agent-1", null);

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Last = request;
            return Task.FromResult(Next);
        }
    }

    static HomeViewModel Build(out RecordingLaunchClient launch, out AppStateStore store, string statePath) {
        launch = new RecordingLaunchClient();
        store = new AppStateStore(statePath);
        return new HomeViewModel(new FakeDaemonClientService(), store, launch);
    }

    [Test]
    public async Task Choosing_a_harness_remembers_it_for_that_repository() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out _, out var store, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("codex");

        var saved = await store.LoadAsync();
        await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");
    }

    [Test]
    public async Task Switching_repository_restores_that_repositorys_harness() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out _, out _, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("codex");
        await vm.SelectRepositoryAsync("/repo/b");
        await vm.ChooseHarnessAsync("kiro");
        await vm.SelectRepositoryAsync("/repo/a");

        await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
    }

    [Test]
    public async Task A_repository_with_no_choice_falls_back_to_the_default_not_the_previous_repo() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out _, out _, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("kiro");
        await vm.SelectRepositoryAsync("/repo/never-seen");

        await Assert.That(vm.SelectedVendor).IsEqualTo(HomeViewModel.DefaultVendor);
    }

    [Test]
    public async Task Not_remembering_leaves_the_stored_choice_untouched() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out _, out var store, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("codex");
        vm.RememberHarness = false;
        await vm.ChooseHarnessAsync("pi");

        await Assert.That(vm.SelectedVendor).IsEqualTo("pi");
        var saved = await store.LoadAsync();
        await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");
    }

    [Test]
    public async Task Start_sends_the_selected_repository_and_harness() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out var launch, out _, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("gemini");
        vm.Goal = "Fix the flaky test";
        await vm.StartCommand.Execute();

        await Assert.That(launch.Last!.RepoPath).IsEqualTo("/repo/a");
        await Assert.That(launch.Last!.Vendor).IsEqualTo("gemini");
        await Assert.That(launch.Last!.Prompt).IsEqualTo("Fix the flaky test");
    }

    [Test]
    public async Task A_failed_start_surfaces_the_servers_reason() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out var launch, out _, path);
        launch.Next = new LaunchOutcome(false, null, "Daemon 'kcap-dev' is at capacity.");

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.StartCommand.Execute();

        await Assert.That(vm.StartError).IsEqualTo("Daemon 'kcap-dev' is at capacity.");
    }

    [Test]
    public async Task The_scratch_target_keeps_its_own_remembered_harness() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out _, out var store, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("codex");
        await vm.SelectRepositoryAsync(HomeViewModel.ScratchRepoPath);
        await vm.ChooseHarnessAsync("claude");

        var saved = await store.LoadAsync();
        await Assert.That(saved.HarnessByRepo![HomeViewModel.ScratchRepoPath]).IsEqualTo("claude");
        await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");

        await vm.SelectRepositoryAsync("/repo/a");
        await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
    }

    [Test]
    public async Task A_successful_start_clears_the_goal_and_any_previous_error() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out var launch, out _, path);
        launch.Next = new LaunchOutcome(false, null, "boom");
        await vm.SelectRepositoryAsync("/repo/a");
        await vm.StartCommand.Execute();

        launch.Next = new LaunchOutcome(true, "agent-2", null);
        vm.Goal = "next thing";
        await vm.StartCommand.Execute();

        await Assert.That(vm.StartError).IsNull();
        await Assert.That(vm.Goal).IsEqualTo("");
    }
}
