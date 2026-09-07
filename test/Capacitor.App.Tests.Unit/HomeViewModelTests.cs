using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// HomeViewModel's Harnesses/Sessions projections ObserveOn(RxSchedulers.MainThreadScheduler)
/// before touching bound state (same reason as MainWindowViewModel/TrayViewModel), so every test
/// here runs inside AvaloniaSession.WithImmediateRxScheduler and carries
/// [NotInParallel("AvaloniaSession")] — see MainWindowViewModelTests' identical header comment.
public class HomeViewModelTests {
    /// The daemon mints agent ids as Guid("N") — 32 hex digits — and a Started outcome carrying
    /// anything else is the "launched but unopenable" case (spec §3), so every launch fixture here
    /// uses real-shaped ids.
    const string LaunchedId = "0123456789abcdef0123456789abcdef";
    const string SecondLaunchedId = "fedcba9876543210fedcba9876543210";

    sealed class RecordingLaunchClient : ILaunchClient {
        public LaunchRequest? Last;
        public LaunchOutcome Next = new(true, LaunchedId, null);

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Last = request;
            return Task.FromResult(Next);
        }
    }

    /// Scripted stand-in for RepoPathStore.GetSortedPathsAsync — the real store reads the
    /// developer's own ~/.config/kcap/repos.json, which no test may touch.
    static Func<Task<string[]>> Known(params string[] paths) => () => Task.FromResult(paths);

    /// Primed connected: StartCommand's canExecute gates on daemon + server both being up, so a
    /// fixture that launches must model the connected steady state.
    static HomeViewModel Build(out RecordingLaunchClient launch, out AppStateStore store, string statePath) {
        launch = new RecordingLaunchClient();
        store = new AppStateStore(statePath);
        var daemon = new FakeDaemonClientService();
        Connect(daemon);
        return new HomeViewModel(daemon, store, launch, Known());
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
    public async Task Start_carries_the_chosen_model_and_effort_and_a_vendor_change_resets_the_model() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.SelectedModel = "claude-fable-5";
            vm.SelectedEffort = "high";
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.Model).IsEqualTo("claude-fable-5");
            await Assert.That(launch.Last!.Effort).IsEqualTo("high");

            // Model ids are vendor-specific; effort's ladder is shared and survives.
            await vm.ChooseHarnessAsync("codex");
            await Assert.That(vm.SelectedModel).IsEqualTo("");
            await Assert.That(vm.SelectedEffort).IsEqualTo("high");

            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.Model).IsEqualTo("");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Permission_mode_defaults_to_manual_which_sends_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(HomeViewModel.DefaultPermissionMode);

            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsNull();
        });
    }

    /// The mode vocabulary is Claude's, so a choice made under Claude never rides a launch of
    /// another vendor — but it is kept, not reset, so switching back restores it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_chosen_permission_mode_is_sent_for_claude_only() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            vm.SelectedPermissionMode = "bypassPermissions";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsEqualTo("bypassPermissions");

            await vm.ChooseHarnessAsync("codex");
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsNull();

            await vm.ChooseHarnessAsync("claude");
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsEqualTo("bypassPermissions");
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
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

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
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

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

    /// Trailing separators are spelling, not identity — a remembered path and a known/agent path
    /// that differ only by `/` must stay one menu row.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_dedups_trailing_separators() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                () => Task.FromResult(new[] { "/repo/kcap-cli/" }));

            await vm.SelectRepositoryAsync("/repo/kcap-cli");
            daemon.Agents.AddOrUpdate(Agent("x", "/repo/kcap-cli/"));

            var repos = (await vm.ListRepositoriesAsync()).Where(r => r.RepoPath.Length > 0).ToList();

            await Assert.That(repos.Count).IsEqualTo(1);
            await Assert.That(repos[0].RepoPath).IsEqualTo("/repo/kcap-cli");
            await Assert.That(repos[0].Selected).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Scratch_appears_only_when_no_real_repositories() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

            var empty = await vm.ListRepositoriesAsync();
            await Assert.That(empty.Count).IsEqualTo(1);
            await Assert.That(empty[0].RepoPath).IsEqualTo(HomeViewModel.ScratchRepoPath);

            daemon.Agents.AddOrUpdate(Agent("x", "/repo/b"));
            var withRepo = await vm.ListRepositoriesAsync();

            await Assert.That(withRepo.Any(r => r.RepoPath.Length == 0)).IsFalse();
            await Assert.That(withRepo.Single(r => r.RepoPath == "/repo/b").Selected).IsTrue();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/b");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Empty_selection_adopts_the_most_recent_known_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            // GetSortedPathsAsync is last-used first — index 0 is the most recent.
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                Known("/repo/newer", "/repo/older"));

            await vm.EnsureDefaultRepositoryAsync();

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/newer");
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Any(r => r.RepoPath.Length == 0)).IsFalse();
            await Assert.That(repos.Single(r => r.RepoPath == "/repo/newer").Selected).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task EnsureDefault_does_not_overwrite_a_pick_made_during_discovery() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var release = new TaskCompletionSource();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                async () => {
                    await release.Task;
                    return ["/repo/default"];
                });

            var ensure = vm.EnsureDefaultRepositoryAsync();
            await vm.SelectRepositoryAsync("/repo/user-picked");
            release.SetResult();
            await ensure;

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/user-picked");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_agent_without_a_repository_contributes_no_entry() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

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
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

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
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

            daemon.Agents.AddOrUpdate(Agent("x", "/x/bravo"));
            daemon.Agents.AddOrUpdate(Agent("y", "/y/alpha"));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos[0].RepoPath).IsEqualTo("/y/alpha");
            await Assert.That(repos[1].RepoPath).IsEqualTo("/x/bravo");
        });
    }

    /// A reviewer launched into a requester's worktree reports that worktree as its RepoPath —
    /// the menu must offer the repository, never the agent's checkout (GH #655).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_agents_worktree_path_is_listed_as_its_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

            daemon.Agents.AddOrUpdate(Agent("x", "/repo/a/.claude/worktrees/leafy"));

            var repos = await vm.ListRepositoriesAsync();

            await Assert.That(repos.Any(r => r.RepoPath == "/repo/a")).IsTrue();
            await Assert.That(repos.Any(r => r.RepoPath.Contains("worktrees"))).IsFalse();
        });
    }

    /// The daemon's persisted known-repos store (repos.json — the same list DaemonConnect.RepoPaths
    /// feeds the server's launch dialog) is a source of its own: a repo you once ran an agent in
    /// must appear with no live agent and no locally remembered harness.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_repository_list_includes_the_daemons_persisted_known_repos() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var vm = new HomeViewModel(
                new FakeDaemonClientService(), new AppStateStore(path), new RecordingLaunchClient(),
                Known("/repo/recorded"));

            var repos = await vm.ListRepositoriesAsync();

            var known = repos.Single(r => r.RepoPath == "/repo/recorded");
            await Assert.That(known.Vendor).IsEqualTo(HomeViewModel.DefaultVendor);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_known_repo_dedups_against_a_remembered_key_the_way_the_filesystem_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                Known("/repo/alpha", "/repo/beta"));

            await vm.SelectRepositoryAsync("/repo/Alpha");
            await vm.ChooseHarnessAsync("codex");

            var repos = (await vm.ListRepositoriesAsync()).Where(r => r.RepoPath.Length > 0).ToList();

            // beta only exists in the store, so it proves the source is wired; alpha collides
            // with the remembered key wherever the filesystem says they are one repository.
            await Assert.That(repos.Any(r => r.RepoPath == "/repo/beta")).IsTrue();
            var expected = OperatingSystem.IsLinux() ? 3 : 2;
            await Assert.That(repos.Count).IsEqualTo(expected);
            await Assert.That(repos.Any(r => r.RepoPath == "/repo/Alpha")).IsTrue();
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
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

            // Before any snapshot: capability unknown, so everything is offered.
            var piBefore = vm.Harnesses.Single(h => h.Vendor == "pi").Available;

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude", "codex"]));

            await Assert.That(piBefore).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "pi").Available).IsFalse();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "claude").Available).IsTrue();
        });
    }

    static void Connect(FakeDaemonClientService daemon, string connection = "connected") {
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: connection));
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_is_disabled_until_daemon_and_server_are_both_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());
            await vm.SelectRepositoryAsync("/repo/a");

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            Connect(daemon);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_lost_server_connection_tells_the_user_to_sign_in() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

            Connect(daemon);
            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.StartButtonTip).IsEqualTo("Start");

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.StartButtonTip).IsEqualTo(HomeViewModel.ServerLostNotice);

            // Transient by definition — the retry resolves it or lands on "disconnected".
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "reconnecting"));
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ConnectingNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
        });
    }

    /// The lane can go SignedOut (a remote HTTP fetch's own 401, ParkSignedOut) independently of
    /// the local daemon's attach state — the sign-in affordance must surface either way.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaneSignedOutSurfacesSignInEvenWithTheLocalDaemonDown() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), laneStatus: lane.Status);

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "not running", null));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));

            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.SignInVisible).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unreachable_daemon_is_not_a_sign_in_problem() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "not running", null));

            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.BannerMessage).IsEqualTo(HomeViewModel.DaemonDownNotice);
            await Assert.That(vm.ConnectionBannerVisible).IsTrue();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
        });
    }

    [Test]
    [Arguments(null, null, null)]
    [Arguments("daemon down", null, "daemon down")]
    [Arguments("daemon down", "kcap too old", "kcap too old")]
    [Arguments(null, "kcap too old", "kcap too old")]
    [Arguments("daemon down", "", "daemon down")]
    public async Task BannerMessage_prefers_a_start_message_over_the_connection_notice(
            string? notice, string? startMessage, string? expected) {
        await Assert.That(HomeViewModel.BannerMessageFor(notice, startMessage)).IsEqualTo(expected);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unauthorized_start_asks_for_sign_in_instead_of_the_raw_error() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var launch = new RecordingLaunchClient();
            var signInRequests = 0;
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), requestSignIn: () => signInRequests++);
            Connect(daemon);
            await vm.SelectRepositoryAsync("/repo/a");
            launch.Next = new LaunchOutcome(
                false, null, "Response status code does not indicate success: 401 (Unauthorized).",
                Unauthorized: true);

            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.SignInExpiredNotice);

            await vm.SignInCommand.Execute();
            await Assert.That(signInRequests).IsEqualTo(1);

            vm.NotifySignInCompleted();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task After_sign_in_a_disconnected_server_shows_finishing_not_sign_in_again() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            using var vm = new HomeViewModel(daemon, new AppStateStore(path), new RecordingLaunchClient(), Known());

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.SignInVisible).IsTrue();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.ServerLostNotice);

            vm.NotifySignInCompleted();
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsEqualTo(HomeViewModel.FinishingSignInNotice);

            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected"));
            await Assert.That(vm.SignInVisible).IsFalse();
            await Assert.That(vm.ConnectionNotice).IsNull();
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

            launch.Next = new LaunchOutcome(true, SecondLaunchedId, null);
            vm.Goal = "next thing";
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.Goal).IsEqualTo("");
        });
    }

    // The launcher's machine picker: name-based launch routing is only defined within one owner,
    // so ListMachinesAsync is the one place that rule is enforced — every test below reads its
    // output rather than trusting a wired-through daemon list.

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PickerOffersLocalPlusOwnConnectedDaemons() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", MachineId = "m2", Connected = true },
                new DaemonInfo { Name = "work-mac", OwnerUserId = "u2", MachineId = "m3", Connected = true },
                // Same machine id + name as the local daemon: the local daemon's own registry
                // twin, never a distinct remote target.
                new DaemonInfo { Name = daemon.DaemonName, OwnerUserId = "u1", MachineId = "m1", Connected = true },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), localMachineId: "m1");

            var options = await vm.ListMachinesAsync();

            await Assert.That(options.Count).IsEqualTo(2);
            await Assert.That(options[0].DaemonName).IsEqualTo(daemon.DaemonName);
            await Assert.That(options[0].IsLocal).IsTrue();
            await Assert.That(options[0].Selected).IsTrue();
            await Assert.That(options[1].DaemonName).IsEqualTo("home-pc");
            await Assert.That(options.Any(o => o.DaemonName == "work-mac")).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task NullViewerIdOffersOnlyLocal() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true }]);
            // No viewerId supplied — the ctor default never guesses ownership.
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(), daemons: remote.Daemons);

            var options = await vm.ListMachinesAsync();

            await Assert.That(options.Count).IsEqualTo(1);
            await Assert.That(options[0].DaemonName).IsEqualTo(daemon.DaemonName);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SelectingRemoteMachineSwitchesRepoAndVendorSources() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var store = new AppStateStore(path);
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/w/repo", "/w/repo2"], SupportedVendors = ["codex"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, store, new RecordingLaunchClient(), Known(),
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc");

            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Count).IsEqualTo(2);
            await Assert.That(repos.Any(r => r.RepoPath == "/w/repo")).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "codex").Available).IsTrue();
            await Assert.That(vm.Harnesses.Single(h => h.Vendor == "claude").Available).IsFalse();
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            // Picking another of the MACHINE's own repo paths must revalidate against that
            // machine's supported vendors — never fall through to the local HarnessByRepo/
            // DefaultVendor rule, which would reset onto "claude" (unsupported here) since
            // "/w/repo2" has no local entry.
            await vm.SelectRepositoryAsync("/w/repo2");
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo2");
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            var saved = await store.LoadAsync();
            await Assert.That(saved.HarnessByRepo is null || saved.HarnessByRepo.Count == 0).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaunchCarriesTheSelectedMachine() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(),
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);

            await vm.SelectMachineAsync("home-pc");
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.DaemonName).IsEqualTo("home-pc");
        });
    }

    /// A remote workspace opens against the LOCAL daemon socket, which can never find a remote
    /// agent — the auto-open must never fire for a launch that targeted a remote machine.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARemoteLaunchNeverAutoOpensTheWorkspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var launch = new RecordingLaunchClient();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            var opened = new List<(string AgentId, int Generation)>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(),
                openSessionIfCurrent: (id, generation) => opened.Add((id, generation)),
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);

            await vm.SelectMachineAsync("home-pc");
            await vm.StartCommand.Execute();

            await Assert.That(opened.Count).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ALocalLaunchStillAutoOpensTheWorkspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient();
            var opened = new List<(string AgentId, int Generation)>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(),
                openSessionIfCurrent: (id, generation) => opened.Add((id, generation)));

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(opened.Count).IsEqualTo(1);
            await Assert.That(opened[0].AgentId).IsEqualTo(LaunchedId);
        });
    }

    /// The owned-remote list can legitimately empty out from under an already-selected remote
    /// machine (a registry blip) — the picker must stay reachable so the user can switch back to
    /// local, rather than hiding itself with no way to change the selection.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PickerStaysVisibleForAnActiveRemoteSelectionEvenWhenTheOwnedListEmpties() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc");
            await WaitUntilAsync(() => vm.MachinePickerVisible, "picker visible after selecting home-pc");

            remote.DaemonsSubject.OnNext([]);
            await Task.Delay(20); // let the CombineLatest re-run over the now-empty owned list
            await Assert.That(vm.MachinePickerVisible).IsTrue();

            await vm.SelectMachineAsync(daemon.DaemonName);
            await Assert.That(vm.RemoteMachineSelected).IsFalse();
        });
    }

    static async Task WaitUntilAsync(Func<bool> condition, string what, int ms = 2000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteSelectionRequiresTheLane() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true }]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);

            await vm.SelectMachineAsync("home-pc");
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
        });
    }

    // Display-list filtering alone doesn't protect the launch path — a name can reach
    // SelectMachineAsync/StartCommand without ever having passed through ListMachinesAsync.

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SelectingAnUnownedNameIsRefused() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "work-mac", OwnerUserId = "u2", Connected = true }]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known(),
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectRepositoryAsync("/repo/a");

            // "work-mac" never passed ListMachinesAsync's own-daemons filter (it's u2's) — calling
            // SelectMachineAsync with it directly (a stale menu row, a forged binding) must be a
            // no-op, not a silent remote selection.
            await vm.SelectMachineAsync("work-mac");

            await Assert.That(vm.RemoteMachineSelected).IsFalse();
            await Assert.That(vm.SelectedMachine).IsEqualTo(daemon.DaemonName);
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/a");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ReassignedDaemonOwnerRevokesAnAlreadySelectedMachine() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            var launch = new RecordingLaunchClient();
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(),
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"), laneStatus: lane.Status);

            await vm.SelectMachineAsync("home-pc");
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            // Same name, now a different owner — a registry refresh racing the earlier selection.
            remote.DaemonsSubject.OnNext([
                new DaemonInfo { Name = "home-pc", OwnerUserId = "u2", Connected = true, RepoPaths = ["/w/repo"] },
            ]);

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            // ReactiveCommand.Execute() does not itself gate on CanExecute (verified directly: an
            // unguarded call here reaches StartAsync regardless) — the actual boundary is
            // StartAsync's own fresh ownership re-check, so calling it straight through must still
            // refuse rather than reach the wire.
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsEqualTo(HomeViewModel.MachineUnavailableMessage);
            await Assert.That(launch.Last).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SwitchingBackToLocalRestoresTheLocalRepoAndRevalidatesTheVendor() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["kiro"]));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new DaemonInfo {
                    Name = "home-pc", OwnerUserId = "u1", Connected = true,
                    RepoPaths = ["/w/repo"], SupportedVendors = ["codex"],
                },
            ]);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(), Known("/repo/local"),
                daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));

            await vm.SelectMachineAsync("home-pc");
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");

            await vm.SelectMachineAsync(daemon.DaemonName);

            await Assert.That(vm.RemoteMachineSelected).IsFalse();
            // The existing local flow (EnsureDefaultRepositoryAsync) adopts the known repo —
            // never the remote machine's path.
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/repo/local");
            var repos = await vm.ListRepositoriesAsync();
            await Assert.That(repos.Any(r => r.RepoPath == "/w/repo")).IsFalse();
            // codex isn't in the local daemon's advertised set ("kiro") — revalidated, not left at
            // whatever the remote machine last set it to.
            await Assert.That(vm.SelectedVendor).IsEqualTo("kiro");
        });
    }

    // Launch-outcome correlation: the accepted id is request-accepted, not success.

    /// Pushes the failure before returning the launch Task, to exercise the race where a
    /// LaunchFailed arrives while StartAsync's invoke is still in flight.
    sealed class FailureBeforeReturnLaunchClient : ILaunchClient {
        public required Subject<LaunchFailure> Failures { get; init; }
        public required string AgentId { get; init; }
        public required string Reason { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Failures.OnNext(new LaunchFailure(AgentId, Reason));
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    [Test]
    public async Task DenialReasonRendersFriendly() =>
        await Assert.That(HomeViewModel.FriendlyLaunchFailure("launch_denied_by_owner: prompt_no_ui"))
            .Contains("consent policy denied");

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LaunchFailureAfterAcceptSetsStartError() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task FailureBeforeInvokeReturnsIsStillApplied() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var failures = new Subject<LaunchFailure>();
            var launch = new FailureBeforeReturnLaunchClient {
                Failures = failures, AgentId = "agent-9", Reason = "launch_denied_by_owner: default",
            };
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ForeignFailuresAreIgnored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("other-id", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RowAppearanceClearsPendingSoLateFailureIsIgnored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, "agent-9", null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(),
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            daemon.Agents.AddOrUpdate(Agent("agent-9", "/repo/a"));
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// The hub can return either id shape (NormalizeAgentId's own comment) — a failure carrying
    /// the DASHED form must still correlate against the "N"-normalized key StartAsync recorded.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task DashedGuidFailureCorrelatesWithTheNormalizedPendingKey() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var dashed = Guid.Parse(LaunchedId).ToString("D");
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, dashed, null) };
            var failures = new Subject<LaunchFailure>();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(), launchFailures: failures);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure(dashed, "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).Contains("consent policy denied");
        });
    }

    /// Same id-shape mismatch as above, on the row-confirmation path: a directory row carrying
    /// the dashed form must still clear the "N"-normalized pending entry.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task DashedGuidRowClearsThePendingEntrySoALateDashedFailureIsIgnored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var dashed = Guid.Parse(LaunchedId).ToString("D");
            var launch = new RecordingLaunchClient { Next = new LaunchOutcome(true, dashed, null) };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(),
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            daemon.Agents.AddOrUpdate(Agent(dashed, "/repo/a"));
            failures.OnNext(new LaunchFailure(dashed, "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// Adds the directory row before the launch call returns, so it exists before RecordPendingLaunch
    /// ever runs — the race a directory-row Add event can never observe (nothing was pending yet).
    sealed class RowBeforeReturnLaunchClient : ILaunchClient {
        public required FakeDaemonClientService Daemon { get; init; }
        public required string AgentId { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Daemon.Agents.AddOrUpdate(Agent(AgentId, request.RepoPath));
            return Task.FromResult(new LaunchOutcome(true, AgentId, null));
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ARowThatPredatesRecordingIsConfirmedImmediately() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var launch = new RowBeforeReturnLaunchClient { Daemon = daemon, AgentId = "agent-9" };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(),
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure("agent-9", "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// Same predates-recording race as above, but with a DASHED-Guid row and a dashed accepted
    /// id — RowExists must compare under NormalizeAgentId, not a raw "local:{id}" key lookup,
    /// since the directory key preserves the row's incoming id spelling verbatim.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ADashedGuidRowThatPredatesRecordingIsConfirmedImmediately() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var daemon = new FakeDaemonClientService();
            Connect(daemon);
            var dashed = Guid.Parse(LaunchedId).ToString("D");
            var launch = new RowBeforeReturnLaunchClient { Daemon = daemon, AgentId = dashed };
            var failures = new Subject<LaunchFailure>();
            using var directory = new AgentDirectory(
                daemon, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                p => p, null, null);
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), launch, Known(),
                launchFailures: failures, directory: directory);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();
            failures.OnNext(new LaunchFailure(dashed, "launch_denied_by_owner: default"));

            await Assert.That(vm.StartError).IsNull();
        });
    }
}
