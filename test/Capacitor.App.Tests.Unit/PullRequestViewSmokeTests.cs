using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class PullRequestViewSmokeTests {
    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(false, true)]
    public Task Reader_is_lazy_and_reusable_for_live_and_ended_sessions_with_or_without_a_terminal(bool terminal, bool ended) => RunOnUiAsync(async () => {
        var daemon = new FakeDaemonClientService();
        var attach = new FakeTerminalAttachClientFactory();
        var time = new FakeTimeProvider();
        var source = new FakePullRequestSource(time);
        var vm = new WorkspaceViewModel("agent", daemon, NewActions(), attach.Factory, () => new FakeTerminalSurface(), time,
            new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource(), pullRequests: source);
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        try {
            daemon.Agents.AddOrUpdate(Agent("agent", "claude", terminal, sessionId: "session") with { Status = ended ? "Completed" : "Running" });
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            vm.PullRequests!.SetForeground(true);
            await WaitUntilAsync(() => vm.PullRequests.CanReveal, what: "PR loaded");
            Dispatcher.UIThread.RunJobs();
            var host = view.FindControl<ContentControl>("PullRequestHost")!;
            var terminalHost = view.FindControl<Control>("TerminalHost");
            var chatHost = view.FindControl<ChatTabView>("ChatHost");
            var chat = vm.Chat;
            if (chat is not null) chat.ComposerText = "Unsent draft";
            await Assert.That(host.Content).IsNull();
            await vm.ShowPullRequestCommand.Execute();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var reader = host.Content;
            await Assert.That(reader).IsTypeOf<PullRequestReader>();
            await Assert.That(((Control)reader!).IsEffectivelyVisible).IsTrue();
            await Assert.That(vm.ShowsTerminalBanners).IsFalse();
            await Assert.That(vm.PullRequests.Description).IsEqualTo("Private description");
            await vm.ShowChatCommand.Execute();
            await vm.ShowPullRequestCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(ReferenceEquals(reader, host.Content)).IsTrue();
            await Assert.That(ReferenceEquals(terminalHost, view.FindControl<Control>("TerminalHost"))).IsTrue();
            await Assert.That(ReferenceEquals(chatHost, view.FindControl<ChatTabView>("ChatHost"))).IsTrue();
            await Assert.That(ReferenceEquals(chat, vm.Chat)).IsTrue();
            if (chat is not null) await Assert.That(chat.ComposerText).IsEqualTo("Unsent draft");
            vm.PullRequests.SetForeground(false);
            await Assert.That(vm.PullRequests.Description).IsNull();
        } finally { window.Close(); await vm.TeardownAsync(); }
    });
}
