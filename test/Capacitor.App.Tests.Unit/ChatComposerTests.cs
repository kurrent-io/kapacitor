using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class ChatComposerTests {
    static async Task<(FakeDaemonClientService Daemon, FakeTimeProvider Time, TerminalTabViewModel Terminal, ChatTabViewModel Chat, FakeTerminalAttachClient Client, RecordingOpener Opener)>
            BuildAttachedAsync() {
        var daemon = new FakeDaemonClientService();
        var factory = new FakeTerminalAttachClientFactory();
        var time = new FakeTimeProvider();
        var opener = new RecordingOpener();
        var terminal = new TerminalTabViewModel("a1", daemon, factory.Factory, () => new FakeTerminalSurface(), time);
        var chat = new ChatTabViewModel("a1", daemon, terminal, TranscriptProjection.For("claude"), opener, time, new FakePermissionService());
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude", "codex"]));
        daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo", model: "claude-opus-5") with { Status = "Running" });
        await (terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
        var client = factory.Created.Single();
        await client.TriggerAttached([]);
        return (daemon, time, terminal, chat, client, opener);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Send_clears_the_text_on_acceptance_and_keeps_it_on_refusal() {
        await RunOnUiAsync(async () => {
            var (_, time, terminal, chat, client, _) = await BuildAttachedAsync();
            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsFalse();

            chat.ComposerText = "hello";
            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsTrue();
            await chat.SendCommand.Execute();
            await Assert.That(chat.ComposerText).IsEqualTo("");
            await Assert.That(chat.ComposerHint).IsEqualTo("Sending…");

            chat.ComposerText = "second";
            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsFalse();
            time.Advance(TimeSpan.FromMilliseconds(150));
            await terminal.PendingDeliveryForTesting!;
            await Assert.That(chat.ComposerText).IsEqualTo("second");
            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(client.SentInput).Count().IsEqualTo(2);
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Hint_follows_send_availability_and_the_vendor_label() {
        await RunOnUiAsync(async () => {
            var (daemon, _, terminal, chat, client, _) = await BuildAttachedAsync();
            await Assert.That(chat.VendorLabel).IsEqualTo("Claude Code");
            await Assert.That(chat.ComposerHint).IsEqualTo("Reply to Claude Code · Enter sends · Shift+Enter for a new line");

            client.Result.SetResult(new AttachOutcome.Detached());
            await terminal.CurrentRunForTesting!;
            await Assert.That(chat.ComposerHint).IsEqualTo("Reattach the terminal to send");

            // Only the run's own Exited/Failed verdict outranks a cache removal, so a removal after
            // a detach lands SessionEnded — and the hint follows the terminal there too.
            daemon.Agents.Remove("a1");
            await Assert.That(chat.ComposerHint).IsEqualTo("This session has ended");

            var attached = TerminalSessionState.Attached(null);
            await Assert.That(ChatTabViewModel.HintFor(SendAvailability.Transitioning, attached, "Claude Code")).IsEqualTo("Updating the terminal connection…");
            await Assert.That(ChatTabViewModel.HintFor(SendAvailability.ReadOnly, TerminalSessionState.Attached("review"), "x")).IsEqualTo("Read-only: review");
            await Assert.That(ChatTabViewModel.HintFor(SendAvailability.Connecting, TerminalSessionState.Connecting, "x")).IsEqualTo("Connecting to the terminal…");
            await Assert.That(ChatTabViewModel.HintFor(SendAvailability.Ended, TerminalSessionState.SessionEnded, "x")).IsEqualTo("This session has ended");
            await Assert.That(ChatTabViewModel.HintFor(SendAvailability.NoTerminal, TerminalSessionState.NotFound, "x")).IsEqualTo("No terminal to send to");
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Footer_reflects_the_dto() {
        await RunOnUiAsync(async () => {
            var (daemon, _, _, chat, _, _) = await BuildAttachedAsync();
            await Assert.That(chat.ModelLabel).IsEqualTo("Claude Opus 5");
            await Assert.That(chat.StatusText).IsEqualTo("Running");
            await Assert.That(chat.StatusDot).IsSameReferenceAs(SessionStatusDots.For("Running"));

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo") with { Status = "Failed" });
            await Assert.That(chat.StatusText).IsEqualTo("Failed");
            await Assert.That(chat.ModelLabel).IsEqualTo("default");
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Links_open_only_through_the_policy_and_an_opener_fault_is_contained() {
        await RunOnUiAsync(async () => {
            var (_, _, _, chat, _, opener) = await BuildAttachedAsync();

            await chat.OpenLinkCommand.Execute("https://example.com/a");
            await chat.OpenLinkCommand.Execute("file:///etc/passwd");
            await chat.OpenLinkCommand.Execute("javascript:alert(1)");
            await Assert.That(opener.Opened).IsEquivalentTo(new[] { "https://example.com/a" });

            opener.ThrowOnOpen = new InvalidOperationException("no browser");
            await chat.OpenLinkCommand.Execute("https://example.com/b");
            await Assert.That(opener.Opened).Count().IsEqualTo(2);
            await chat.TeardownAsync();
        });
    }

    /// Thread identity: the hint's own change lands on the UI thread even when the terminal's
    /// state flips from a pool thread.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pool_thread_state_flip_updates_the_hint_on_the_ui_thread() {
        var onUi = await DispatchAsync(async () => {
            var (_, _, terminal, chat, client, _) = await BuildAttachedAsync();
            bool? hintChangedOnUi = null;
            chat.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChatTabViewModel.ComposerHint)) hintChangedOnUi = Dispatcher.UIThread.CheckAccess(); };

            await Task.Run(() => client.Result.SetResult(new AttachOutcome.Exited(0)));
            await terminal.CurrentRunForTesting!;
            await WaitUntilAsync(() => chat.ComposerHint == "This session has ended", what: "hint");
            await chat.TeardownAsync();
            return hintChangedOnUi;
        });
        await Assert.That(onUi).IsTrue();
    }
}
