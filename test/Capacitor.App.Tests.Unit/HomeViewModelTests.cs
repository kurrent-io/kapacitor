using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// HomeViewModel's Harnesses/Sessions projections ObserveOn(RxSchedulers.MainThreadScheduler)
/// before touching bound state (same reason as MainWindowViewModel/TrayViewModel), so every test
/// here runs inside AvaloniaSession.WithImmediateRxScheduler and carries
/// [NotInParallel("AvaloniaSession")] — see MainWindowViewModelTests' identical header comment.
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

    /// Repo keys compare the way the filesystem does — so the SAME repository reached under
    /// different casing restores its harness on Windows/macOS, and stays distinct on Linux where
    /// two such paths really are two repositories. Asserting the platform's own answer rather than
    /// one hardcoded expectation is what lets this run on every CI leg.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repo_keys_compare_the_way_the_filesystem_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out var store, path);

            await vm.SelectRepositoryAsync("/repo/Alpha");
            await vm.ChooseHarnessAsync("codex");
            await vm.SelectRepositoryAsync("/repo/alpha");

            var expected = OperatingSystem.IsLinux() ? HomeViewModel.DefaultVendor : "codex";
            await Assert.That(vm.SelectedVendor).IsEqualTo(expected);

            // And re-choosing under the other casing must overwrite, not accumulate a second
            // shadowing entry, wherever the two paths are the same repository.
            await vm.ChooseHarnessAsync("pi");
            var saved = await store.LoadAsync();
            var expectedKeys = OperatingSystem.IsLinux() ? 2 : 1;
            await Assert.That(saved.HarnessByRepo!.Count).IsEqualTo(expectedKeys);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Choosing_a_harness_remembers_it_for_that_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out var store, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");

            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Switching_repository_restores_that_repositorys_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            await vm.SelectRepositoryAsync("/repo/b");
            await vm.ChooseHarnessAsync("kiro");
            await vm.SelectRepositoryAsync("/repo/a");

            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_repository_with_no_choice_falls_back_to_the_default_not_the_previous_repo() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("kiro");
            await vm.SelectRepositoryAsync("/repo/never-seen");

            await Assert.That(vm.SelectedVendor).IsEqualTo(HomeViewModel.DefaultVendor);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Not_remembering_leaves_the_stored_choice_untouched() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out _, out var store, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            vm.RememberHarness = false;
            await vm.ChooseHarnessAsync("pi");

            await Assert.That(vm.SelectedVendor).IsEqualTo("pi");
            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_sends_the_selected_repository_and_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("gemini");
            vm.Goal = "Fix the flaky test";
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.RepoPath).IsEqualTo("/repo/a");
            await Assert.That(launch.Last!.Vendor).IsEqualTo("gemini");
            await Assert.That(launch.Last!.Prompt).IsEqualTo("Fix the flaky test");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_start_surfaces_the_servers_reason() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            launch.Next = new LaunchOutcome(false, null, "Daemon 'kcap-dev' is at capacity.");

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsEqualTo("Daemon 'kcap-dev' is at capacity.");
        });
    }

    /// Storage isolation only: ScratchRepoPath ("") is a reserved HarnessByRepo key that must not
    /// share or clobber a real repository's remembered vendor. It says nothing about launching it
    /// — the daemon does not accept a repo-less launch (HomeViewModel.ScratchRepoPath).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_scratch_target_keeps_its_own_remembered_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
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
        });
    }

    static AgentStatusDto Agent(string id, string? repoPath) => new(
        id, "agent", "claude", repoPath, "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_merges_remembered_repos_and_agent_repos() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient());

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            daemon.Agents.AddOrUpdate(Agent("x", "/repo/b"));

            var repos = await vm.ListRepositoriesAsync();

            var a = repos.Single(r => r.RepoPath == "/repo/a");
            var b = repos.Single(r => r.RepoPath == "/repo/b");
            await Assert.That(a.Vendor).IsEqualTo("codex");
            await Assert.That(b.Vendor).IsEqualTo(HomeViewModel.DefaultVendor);
            await Assert.That(a.Selected).IsTrue();
            await Assert.That(b.Selected).IsFalse();
        });
    }

    /// Same platform-follows-filesystem rule as the harness-memory test above: a daemon-supplied
    /// path and a remembered key differing only in case are one repository on Windows/macOS and
    /// two on Linux. This merge is exactly the case that motivated PathComparer.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_dedups_the_way_the_filesystem_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient());

            await vm.SelectRepositoryAsync("/repo/Alpha");
            await vm.ChooseHarnessAsync("codex");
            daemon.Agents.AddOrUpdate(Agent("x", "/repo/alpha"));

            var repos = (await vm.ListRepositoriesAsync()).Where(r => r.RepoPath.Length > 0).ToList();

            var expected = OperatingSystem.IsLinux() ? 2 : 1;
            await Assert.That(repos.Count).IsEqualTo(expected);
            // The remembered key is the one the user picked, so its casing is the one displayed.
            await Assert.That(repos.Any(r => r.RepoPath == "/repo/Alpha")).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_scratch_target_is_always_the_last_repository_entry() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient());

            await vm.SelectRepositoryAsync(HomeViewModel.ScratchRepoPath);
            await vm.ChooseHarnessAsync("pi");
            daemon.Agents.AddOrUpdate(Agent("x", "/repo/b"));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos[^1].RepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
            await Assert.That(repos[^1].Vendor).IsEqualTo("pi");
            await Assert.That(repos[^1].Selected).IsTrue();
            await Assert.That(repos.Count(r => r.RepoPath.Length == 0)).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_agent_without_a_repository_contributes_no_entry() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient());

            daemon.Agents.AddOrUpdate(Agent("x", null));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos.Count).IsEqualTo(1);
            await Assert.That(repos[0].RepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);
        });
    }

    /// A repository picked through the folder dialog but not yet remembered (and with no agent in
    /// it) must still appear — otherwise closing and reopening the menu would lose it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_current_selection_appears_even_when_unsaved_and_agentless() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient());

            vm.RememberHarness = false;
            await vm.SelectRepositoryAsync("/repo/fresh");

            var repos = await vm.ListRepositoriesAsync();

            var fresh = repos.Single(r => r.RepoPath == "/repo/fresh");
            await Assert.That(fresh.Vendor).IsEqualTo(HomeViewModel.DefaultVendor);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repositories_are_ordered_by_leaf_name() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient());

            daemon.Agents.AddOrUpdate(Agent("x", "/x/bravo"));
            daemon.Agents.AddOrUpdate(Agent("y", "/y/alpha"));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos[0].RepoPath).IsEqualTo("/y/alpha");
            await Assert.That(repos[1].RepoPath).IsEqualTo("/x/bravo");
        });
    }

    /// The daemon's advertised vendor set is what narrows the picker, end to end: a snapshot
    /// arriving on the Snapshots stream must reach the bound Harnesses list.
    /// HostedHarnessCatalogTests covers Build in isolation; this covers the wire into it, which
    /// nothing else did.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_daemon_snapshot_narrows_the_harness_picker() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient());

            // Before any snapshot: capability unknown, so everything is offered.
            var piBefore = vm.Harnesses.Single(h => h.Vendor == "pi").Available;

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude", "codex"]));

            await Assert.That(piBefore).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "pi").Available).IsFalse();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "claude").Available).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_successful_start_clears_the_goal_and_any_previous_error() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
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
        });
    }
}
