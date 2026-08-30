using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class PermissionCardViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Detail_is_relative_to_the_root_and_re_renders_when_the_root_arrives_late() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var root = new BehaviorSubject<string?>(null);
            using var svc = new FakePermissionService();
            using var card = new PermissionCardViewModel(
                PermissionEntries.Entry(toolName: "Read", toolInputJson: """{"file_path":"/repo/x/src/a.cs"}"""), svc, root);
            await Assert.That(card.Detail).IsEqualTo("/repo/x/src/a.cs");
            root.OnNext("/repo/x");
            await Assert.That(card.Detail).IsEqualTo("src/a.cs");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Omitted_input_and_empty_tool_name_have_their_own_text() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var svc = new FakePermissionService();
            using var card = new PermissionCardViewModel(PermissionEntries.Entry(toolName: "", toolInputJson: null, omitted: true), svc, new BehaviorSubject<string?>(null));
            await Assert.That(card.ToolName).IsEqualTo("Tool call");
            await Assert.That(card.Detail).IsEqualTo("Input too large to show");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Allow_always_shows_for_claude_only() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var svc = new FakePermissionService();
            using var claude = new PermissionCardViewModel(PermissionEntries.Entry(vendor: "claude"), svc, new BehaviorSubject<string?>(null));
            using var codex  = new PermissionCardViewModel(PermissionEntries.Entry(vendor: "codex"), svc, new BehaviorSubject<string?>(null));
            await Assert.That(claude.ShowsAllowAlways).IsTrue();
            await Assert.That(codex.ShowsAllowAlways).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Commands_resolve_with_the_answer_and_a_transport_failure_re_enables_with_an_error_line() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var svc = new FakePermissionService();
            var entry = PermissionEntries.Entry();
            svc.Add(entry);
            using var card = new PermissionCardViewModel(entry, svc, new BehaviorSubject<string?>(null));

            var gate = svc.Arm();
            var run = card.AllowAlwaysCommand.Execute().ToTask();
            await WaitUntilAsync(() => card.IsBusy, what: "busy while in flight");
            gate.SetResult(new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, "daemon_unreachable"));
            await run;
            await Assert.That(card.IsBusy).IsFalse();
            await Assert.That(card.ErrorText).IsEqualTo("Daemon unreachable — try again");
            await Assert.That(svc.Resolved[0].Answer).IsEqualTo(PermissionAnswer.AllowAlways);

            svc.Queue(PermissionResolveKind.Applied);
            await card.DenyCommand.Execute().ToTask();
            await Assert.That(svc.Resolved[1].Answer).IsEqualTo(PermissionAnswer.Deny);
            await Assert.That(svc.Cache.Count).IsEqualTo(0);
        });
    }
}
