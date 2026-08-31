using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class QuestionCardViewModelTests {
    const string SingleSelect = """{"questions":[{"question":"Pick","header":"Choice","options":[{"label":"A","description":"first"},{"label":"B"}]}]}""";
    const string FreeTextOnly = """{"questions":[{"question":"Say"}]}""";
    const string MultiAndSingle = """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]},{"question":"Tags","multiSelect":true,"options":[{"label":"X"},{"label":"Y"}]}]}""";

    static (FakePermissionService Svc, QuestionCardViewModel Card) Make(string input, string requestId = "q1") {
        var svc = new FakePermissionService();
        var entry = PermissionEntries.Question(requestId, toolInputJson: input);
        svc.Add(entry);
        return (svc, new QuestionCardViewModel(entry, svc));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Fast_path_submits_on_option_click() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) using (card) {
                await Assert.That(card.IsFastPath).IsTrue();
                await Assert.That(card.ShowsSubmit).IsFalse();
                svc.Queue(PermissionResolveKind.Applied);
                await card.Questions[0].Options[1].PickCommand.Execute().ToTask();
                await Assert.That(svc.Answered[0].Answers[0].SelectedLabels).IsEquivalentTo(["B"]);
                await Assert.That(svc.Answered[0].Answers[0].OtherText).IsNull();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Fast_path_other_text_submits_on_enter_and_shows_the_inline_answer() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) using (card) {
                var group = card.Questions[0];
                await Assert.That(group.ShowsOtherAnswer).IsFalse();
                group.OtherText = "my own";
                await Assert.That(group.ShowsOtherAnswer).IsTrue();
                svc.Queue(PermissionResolveKind.Applied);
                await group.EnterCommand.Execute().ToTask();
                await Assert.That(svc.Answered[0].Answers[0].OtherText).IsEqualTo("my own");
                await Assert.That(svc.Answered[0].Answers[0].SelectedLabels.Count).IsEqualTo(0);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Free_text_only_is_not_the_fast_path_and_whitespace_does_not_answer() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(FreeTextOnly);
            using (svc) using (card) {
                await Assert.That(card.IsFastPath).IsFalse();
                await Assert.That(card.ShowsSubmit).IsTrue();
                card.Questions[0].OtherText = "   ";
                await Assert.That(card.Questions[0].IsAnswered).IsFalse();
                await Assert.That(card.Questions[0].ShowsOtherAnswer).IsFalse();
                card.Questions[0].OtherText = "hello";
                await Assert.That(card.Questions[0].IsAnswered).IsTrue();
                await Assert.That(card.Questions[0].ShowsOtherAnswer).IsFalse();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Submit_gates_on_every_question_and_sends_all_answers() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(MultiAndSingle);
            using (svc) using (card) {
                await Assert.That(await card.SubmitCommand.CanExecute.FirstAsync()).IsFalse();
                card.Questions[0].Options[0].IsSelected = true;
                await Assert.That(await card.SubmitCommand.CanExecute.FirstAsync()).IsFalse();
                card.Questions[1].Options[0].IsSelected = true;
                card.Questions[1].Options[1].IsSelected = true;
                await Assert.That(await card.SubmitCommand.CanExecute.FirstAsync()).IsTrue();

                svc.Queue(PermissionResolveKind.Applied);
                await card.SubmitCommand.Execute().ToTask();
                var answers = svc.Answered[0].Answers;
                await Assert.That(answers[0].SelectedLabels).IsEquivalentTo(["A"]);
                await Assert.That(answers[1].SelectedLabels).IsEquivalentTo(["X", "Y"]);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Single_select_pick_and_other_text_displace_each_other() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(MultiAndSingle);
            using (svc) using (card) {
                var group = card.Questions[0];
                group.Options[0].IsSelected = true;
                group.OtherText = "custom";
                await Assert.That(group.Options[0].IsSelected).IsFalse();
                await group.Options[1].PickCommand.Execute().ToTask();
                await Assert.That(group.OtherText).IsEqualTo("");
                await Assert.That(group.Options[1].IsSelected).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Double_activation_sends_once_and_transport_failure_re_enables() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) using (card) {
                var gate = svc.Arm();
                var first = card.Questions[0].Options[0].PickCommand.Execute().ToTask();
                await WaitUntilAsync(() => card.IsBusy, what: "busy in flight");
                await card.Questions[0].Options[1].PickCommand.Execute().ToTask();
                gate.SetResult(new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, "daemon_unreachable"));
                await first;
                await Assert.That(svc.Answered.Count).IsEqualTo(1);
                await Assert.That(card.IsBusy).IsFalse();
                await Assert.That(card.ErrorText).IsEqualTo("Daemon unreachable — try again");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Eviction_mid_flight_leaves_no_error_and_no_post_disposal_notification() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) {
                var gate = svc.Arm();
                var run = card.Questions[0].Options[0].PickCommand.Execute().ToTask();
                await WaitUntilAsync(() => card.IsBusy, what: "busy in flight");

                var notified = new List<string>();
                card.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? "");
                card.Dispose();
                gate.SetResult(new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, "daemon_unreachable"));
                await run;
                await Assert.That(notified).IsEmpty();
                await Assert.That(card.ErrorText).IsNull();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_throwing_service_clears_busy_and_shows_the_generic_line() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) using (card) {
                svc.Arm().SetException(new ArgumentException("composer rejected"));
                await card.Questions[0].Options[0].PickCommand.Execute().ToTask();
                await Assert.That(card.IsBusy).IsFalse();
                await Assert.That(card.ErrorText).IsEqualTo("Something went wrong — try again");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_cancellation_not_from_disposal_shows_the_generic_line() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (svc, card) = Make(SingleSelect);
            using (svc) using (card) {
                svc.Arm().SetException(new OperationCanceledException());
                await card.Questions[0].Options[0].PickCommand.Execute().ToTask();
                await Assert.That(card.IsBusy).IsFalse();
                await Assert.That(card.ErrorText).IsEqualTo("Something went wrong — try again");
            }
        });
    }
}
