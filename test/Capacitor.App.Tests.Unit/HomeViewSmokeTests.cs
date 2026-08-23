using System.Collections;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the Home tab (Task 6, AI-2194). HomeView is a UserControl,
/// not a Window (unlike MainWindow) — each test hosts it inside a plain Window purely to give
/// headless something to Show(); session setup and control lookup otherwise copy
/// MainWindowSmokeTests exactly (see that file's own header comment).
public class HomeViewSmokeTests {
    sealed class RecordingLaunchClient : ILaunchClient {
        public LaunchOutcome Next = new(true, "agent-1", null);

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
            Task.FromResult(Next);
    }

    static (HomeView View, HomeViewModel Vm, FakeDaemonClientService Service, RecordingLaunchClient Launch, TempDir Tmp) Build() {
        var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var service = new FakeDaemonClientService();
        var launch = new RecordingLaunchClient();
        var vm = new HomeViewModel(service, new AppStateStore(path), launch);
        return (new HomeView { DataContext = vm }, vm, service, launch, tmp);
    }

    static T? Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    // ItemsControl.ItemsSource is an IEnumerable?; Sessions (ReadOnlyObservableCollection<T>) is
    // also an ICollection, so this reads the bound source's count directly — no dependency on a
    // realized visual tree / layout pass.
    static int ItemCount(ItemsControl items) => items.ItemsSource is ICollection c ? c.Count : -1;

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task HomeView_resolves_all_six_named_controls() {
        var found = await AvaloniaSession.DispatchAsync(() => {
            var (view, vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var names = new[] { "GoalInput", "RepositoryChip", "HarnessChip", "StartButton", "StartErrorText", "SessionCards" };
            var resolved = names.ToDictionary(name => name, name => Find<Control>(window, name) is not null);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return resolved;
        });

        await Assert.That(found["GoalInput"]).IsTrue();
        await Assert.That(found["RepositoryChip"]).IsTrue();
        await Assert.That(found["HarnessChip"]).IsTrue();
        await Assert.That(found["StartButton"]).IsTrue();
        await Assert.That(found["StartErrorText"]).IsTrue();
        await Assert.That(found["SessionCards"]).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartButton_is_enabled_only_once_a_repository_is_selected() {
        var (enabledBefore, enabledAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var (view, vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var startButton = Find<Button>(window, "StartButton")!;
            var before = startButton.IsEnabled;

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            Dispatcher.UIThread.RunJobs();
            var after = startButton.IsEnabled;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before, after);
        });

        await Assert.That(enabledBefore).IsFalse();
        await Assert.That(enabledAfter).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartErrorText_visibility_follows_StartError() {
        var (visibleBefore, visibleAfterFailure, errorMessage, visibleAfterSuccess) = await AvaloniaSession.DispatchAsync(async () => {
            var (view, vm, _, launch, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var errorText = Find<TextBlock>(window, "StartErrorText")!;
            var before = errorText.IsVisible;

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            launch.Next = new LaunchOutcome(false, null, "Daemon 'kcap-dev' is at capacity.");
            await vm.StartCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var afterFailure = errorText.IsVisible;
            var message = errorText.Text;

            launch.Next = new LaunchOutcome(true, "agent-2", null);
            await vm.StartCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var afterSuccess = errorText.IsVisible;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before, afterFailure, message, afterSuccess);
        });

        await Assert.That(visibleBefore).IsFalse();
        await Assert.That(visibleAfterFailure).IsTrue();
        await Assert.That(errorMessage).IsEqualTo("Daemon 'kcap-dev' is at capacity.");
        await Assert.That(visibleAfterSuccess).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SessionCards_item_count_tracks_the_agent_cache() {
        var (countEmpty, countAfterOne, countAfterTwo) = await AvaloniaSession.DispatchAsync(() => {
            var (view, vm, service, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var sessionCards = Find<ItemsControl>(window, "SessionCards")!;
            var empty = ItemCount(sessionCards);

            service.Agents.AddOrUpdate(new AgentStatusDto(
                "a", "agent", "claude", "/repos/kcap-cli", "Running", null, null, null, DateTime.UtcNow, null, null));
            Dispatcher.UIThread.RunJobs();
            var afterOne = ItemCount(sessionCards);

            service.Agents.AddOrUpdate(new AgentStatusDto(
                "b", "agent", "codex", "/repos/other", "Running", null, null, null, DateTime.UtcNow, null, null));
            Dispatcher.UIThread.RunJobs();
            var afterTwo = ItemCount(sessionCards);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (empty, afterOne, afterTwo);
        });

        await Assert.That(countEmpty).IsEqualTo(0);
        await Assert.That(countAfterOne).IsEqualTo(1);
        await Assert.That(countAfterTwo).IsEqualTo(2);
    }
}
