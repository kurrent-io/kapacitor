using Capacitor.App.ViewModels;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The group in isolation: folding, summary, failure, and the visible-list swap. Under the
/// session constraint because ToggleCommand is a ReactiveCommand.
public class ToolGroupItemTests {
    static ToolCallItem Call(string name, ToolCategory category) => new(name, "", category);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_calls_show_until_they_settle_and_the_summary_appears_with_the_first_settlement() {
        await RunOnUiAsync(async () => {
            var group = new ToolGroupItem();
            var a = Call("Bash", ToolCategory.Command);
            var b = Call("Read", ToolCategory.Read);
            group.Add(a);
            group.Add(b);
            await Assert.That(group.HasSummary).IsFalse();
            await Assert.That(group.Summary).IsEqualTo("");
            await Assert.That(group.LiveCalls).IsEquivalentTo(new[] { a, b }, CollectionOrdering.Matching);
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { a, b }, CollectionOrdering.Matching);

            b.Outcome = ToolOutcome.Done;
            await Assert.That(group.HasSummary).IsTrue();
            await Assert.That(group.Summary).IsEqualTo("Read a file");
            await Assert.That(group.LiveCalls).IsEquivalentTo(new[] { a });
            await Assert.That(group.Calls).IsEquivalentTo(new[] { a, b }, CollectionOrdering.Matching);

            a.Outcome = ToolOutcome.Error;
            await Assert.That(group.LiveCalls).IsEmpty();
            await Assert.That(group.Summary).IsEqualTo("Ran a command, read a file");
            await Assert.That(group.SummaryLine).IsEqualTo("Ran a command, read a file · Bash");
            await Assert.That(group.HasFailure).IsTrue();
            await Assert.That(group.ShowsSummaryHeader).IsTrue();

            group.Toggle();
            await Assert.That(group.SummaryLine).IsEqualTo("Ran a command, read a file");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_lone_settled_call_stays_visible_without_summary_chrome() {
        await RunOnUiAsync(async () => {
            var group = new ToolGroupItem();
            var call = Call("Bash", ToolCategory.Command);
            group.Add(call);
            call.Outcome = ToolOutcome.Done;

            await Assert.That(group.HasSummary).IsTrue();
            await Assert.That(group.ShowsSummaryHeader).IsFalse();
            await Assert.That(group.ShowsKindChip).IsTrue();
            await Assert.That(group.KindChip).IsEqualTo("Command");
            await Assert.That(group.LoneCall).IsSameReferenceAs(call);
            await Assert.That(call.ShowRowStatus).IsFalse();
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { call });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Folded_summary_peeks_the_first_settled_detail_and_caps_long_ones() {
        await RunOnUiAsync(async () => {
            var group = new ToolGroupItem();
            var longDetail = new string('x', 80);
            var first = new ToolCallItem("Bash", longDetail, ToolCategory.Command);
            var second = Call("Read", ToolCategory.Read);
            group.Add(first);
            group.Add(second);
            first.Outcome = ToolOutcome.Done;
            second.Outcome = ToolOutcome.Done;

            await Assert.That(group.SummaryLine).IsEqualTo($"Ran a command, read a file · {new string('x', 55)}…");
            group.Toggle();
            await Assert.That(group.SummaryLine).IsEqualTo("Ran a command, read a file");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Toggle_swaps_the_visible_list_between_live_and_every_call() {
        await RunOnUiAsync(async () => {
            var group = new ToolGroupItem();
            var settled = Call("Bash", ToolCategory.Command);
            var live = Call("Read", ToolCategory.Read);
            group.Add(settled);
            group.Add(live);
            settled.Outcome = ToolOutcome.Done;
            var raised = new List<string?>();
            group.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            await Assert.That(group.IsExpanded).IsFalse();
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { live });

            group.Toggle();
            await Assert.That(group.IsExpanded).IsTrue();
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { settled, live }, CollectionOrdering.Matching);
            await Assert.That(raised).Contains(nameof(ToolGroupItem.VisibleCalls));

            group.ToggleCommand.Execute().Subscribe();
            await Assert.That(group.IsExpanded).IsFalse();
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { live });
        });
    }

    [Test]
    public async Task A_call_glyph_shows_the_question_mark_only_while_running_and_awaiting() {
        var call = Call("Bash", ToolCategory.Command);
        await Assert.That(call.OutcomeGlyph).IsEqualTo("");
        await Assert.That(call.IsRunning).IsTrue();
        await Assert.That(call.HasDetail).IsFalse();
        call.IsAwaitingPermission = true;
        await Assert.That(call.OutcomeGlyph).IsEqualTo("?");
        await Assert.That(call.IsSettled).IsFalse();
        await Assert.That(call.IsRunning).IsFalse();
        call.Outcome = ToolOutcome.Done;
        await Assert.That(call.OutcomeGlyph).IsEqualTo("✓");
        await Assert.That(call.IsSettled).IsTrue();
        await Assert.That(call.IsRunning).IsFalse();
        call.IsAwaitingPermission = false;
        await Assert.That(call.OutcomeGlyph).IsEqualTo("✓");
    }

    [Test]
    public async Task Line_text_prefers_detail_over_the_raw_tool_name() {
        var withDetail = new ToolCallItem("Bash", "ls -la", ToolCategory.Command);
        await Assert.That(withDetail.HasDetail).IsTrue();
        await Assert.That(withDetail.LineText).IsEqualTo("ls -la");
        var bare = Call("Bash", ToolCategory.Command);
        await Assert.That(bare.HasDetail).IsFalse();
        await Assert.That(bare.LineText).IsEqualTo("Bash");
    }
}
