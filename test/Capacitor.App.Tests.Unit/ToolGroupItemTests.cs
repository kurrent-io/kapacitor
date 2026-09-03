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
            await Assert.That(group.HasFailure).IsTrue();
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
        call.IsAwaitingPermission = true;
        await Assert.That(call.OutcomeGlyph).IsEqualTo("?");
        await Assert.That(call.IsSettled).IsFalse();
        call.Outcome = ToolOutcome.Done;
        await Assert.That(call.OutcomeGlyph).IsEqualTo("✓");
        await Assert.That(call.IsSettled).IsTrue();
        call.IsAwaitingPermission = false;
        await Assert.That(call.OutcomeGlyph).IsEqualTo("✓");
    }
}
