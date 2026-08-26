using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

// The return half of the lane, and the only payload this CLI acts on. Every rule here is about what
// may cross: kcap setup writes Claude Code hooks and a hook entry is a command string Claude Code
// runs, so a vendor key is mapped onto a harness this build knows or it is dropped.
public class FirstRunAgentsAnswerTests {
    static FirstRunFlowResponse View(
            List<FirstRunAgentChoiceResponse>? agents, DateTimeOffset? decidedAt) =>
        new() {
            FlowId = "f", Step = "Done", CanFinish = true,
            Steps  = new() {
                ["SignIn"] = "Completed", ["Agents"] = "Completed", ["Import"] = "Skipped", ["Done"] = "Completed"
            },
            Agents = agents, AgentsDecidedAt = decidedAt
        };

    static FirstRunAgentChoiceResponse Choice(string vendor, bool record = true, bool tools = true) =>
        new() { Vendor = vendor, Record = record, Tools = tools };

    static readonly DateTimeOffset Decided = new(2026, 8, 25, 9, 30, 0, TimeSpan.Zero);

    [Test]
    public async Task Reads_an_answer_this_build_understands() {
        var answer = FirstRunFlowOutcomes.Agents(View([Choice("claude"), Choice("cursor", tools: false)], Decided));

        await Assert.That(answer!.Choices.Count).IsEqualTo(2);
        await Assert.That(answer.Records("claude")).IsTrue();
        await Assert.That(answer.Tools("claude")).IsTrue();
        await Assert.That(answer.Records("cursor")).IsTrue();
        await Assert.That(answer.Tools("cursor")).IsFalse();
        await Assert.That(answer.IsDecline).IsFalse();
        await Assert.That(answer.Unrecognised).IsEqualTo(0);
    }

    // "Not yet answered" against "Not now". A CLI that reads them alike either installs nothing on a
    // flow nobody has been asked about, or waits forever on a decline.
    [Test]
    public async Task Reads_an_absent_decision_as_unanswered() =>
        await Assert.That(FirstRunFlowOutcomes.Agents(View(null, null))).IsNull();

    [Test]
    public async Task Reads_an_empty_decision_as_a_decline() {
        var answer = FirstRunFlowOutcomes.Agents(View([], Decided));

        await Assert.That(answer).IsNotNull();
        await Assert.That(answer!.IsDecline).IsTrue();
    }

    // An old CLI must not forward a value a newer server invented. The rest of the answer still
    // applies — one unknown vendor is no reason to abandon the eight this build does know.
    [Test]
    public async Task Drops_a_vendor_this_build_has_never_heard_of_and_keeps_the_rest() {
        var answer = FirstRunFlowOutcomes.Agents(View([Choice("claude"), Choice("kimi")], Decided));

        await Assert.That(answer!.Choices.Count).IsEqualTo(1);
        await Assert.That(answer.Records("claude")).IsTrue();
        await Assert.That(answer.Records("kimi")).IsFalse();
        await Assert.That(answer.Unrecognised).IsEqualTo(1);
    }

    // Dropping every entry leaves nothing to install, and that is NOT the same as being asked for
    // nothing — one is a choice, the other is this build being behind the server.
    [Test]
    public async Task Does_not_read_an_answer_it_could_not_understand_as_a_decline() {
        var answer = FirstRunFlowOutcomes.Agents(View([Choice("kimi")], Decided));

        await Assert.That(answer!.Choices).IsEmpty();
        await Assert.That(answer.IsDecline).IsFalse();
    }

    // Half a decision has no identity to apply it against, and applying it would install without one.
    [Test]
    public async Task Reads_a_decision_missing_half_its_wire_shape_as_unanswered() {
        await Assert.That(FirstRunFlowOutcomes.Agents(View([Choice("claude")], null))).IsNull();
        await Assert.That(FirstRunFlowOutcomes.Agents(View(null, Decided))).IsNull();
    }

    [Test]
    public async Task Drops_a_harness_that_was_left_alone() {
        var answer = FirstRunFlowOutcomes.Agents(
            View([Choice("claude"), Choice("cursor", record: false, tools: false)], Decided));

        await Assert.That(answer!.Choices.Count).IsEqualTo(1);
        await Assert.That(answer.IsDecline).IsFalse();
    }

    // The neither-drop has to run BEFORE the duplicate guard: a leading neither-entry would otherwise
    // claim the vendor's slot and the real choice behind it would be dropped as the duplicate, leaving
    // the user's actual selection nowhere.
    [Test]
    public async Task Does_not_let_a_left_alone_entry_swallow_a_real_one_for_the_same_vendor() {
        var answer = FirstRunFlowOutcomes.Agents(
            View([Choice("claude", record: false, tools: false), Choice("claude")], Decided));

        await Assert.That(answer!.Choices.Count).IsEqualTo(1);
        await Assert.That(answer.Records("claude")).IsTrue();
    }

    [Test]
    public async Task Keeps_the_first_of_two_entries_naming_one_vendor() {
        var answer = FirstRunFlowOutcomes.Agents(
            View([Choice("claude", tools: true), Choice("claude", tools: false)], Decided));

        await Assert.That(answer!.Choices.Count).IsEqualTo(1);
        await Assert.That(answer.Tools("claude")).IsTrue();
    }

    [Test]
    public async Task Names_the_chosen_harnesses_in_catalogue_order() {
        var answer = FirstRunFlowOutcomes.Agents(View([Choice("cursor"), Choice("claude")], Decided));

        await Assert.That(answer!.Labels.ToList()).IsEquivalentTo(new[] { "Claude Code", "Cursor" });
    }

    // Consent was given before the tab closed, and the browser settles its step on the decision being
    // recorded rather than on the install finishing — so the work is still ours to do.
    [Test]
    public async Task Takes_the_decision_from_a_leg_the_user_stopped_waiting_on() {
        var view = View([Choice("claude")], Decided);

        await Assert.That(FirstRunFlowOutcomes.Agents(new FirstRunFlowResult.Dismissed(view))).IsNotNull();
        await Assert.That(FirstRunFlowOutcomes.Agents(new FirstRunFlowResult.Abandoned(view))).IsNotNull();
        await Assert.That(FirstRunFlowOutcomes.Agents(new FirstRunFlowResult.Finished(view))).IsNotNull();
    }

    // The decision and the step's outcome are separate fields, so a view can carry choices for a step
    // still being answered. Acting on those applies a half-made choice.
    [Test]
    public async Task Ignores_a_decision_whose_step_has_not_settled() {
        var midAnswer = new FirstRunFlowResponse {
            FlowId = "f", Step = "Agents", CanFinish = true,
            Steps = new() { ["SignIn"] = "Completed", ["Agents"] = "Active" },
            Agents = [Choice("claude")], AgentsDecidedAt = Decided
        };

        await Assert.That(FirstRunFlowOutcomes.Agents(new FirstRunFlowResult.Dismissed(midAnswer))).IsNull();
        await Assert.That(FirstRunFlowOutcomes.Agents(new FirstRunFlowResult.Abandoned(midAnswer))).IsNull();
    }

    [Test]
    public async Task Has_no_decision_for_a_leg_that_never_reached_one() {
        await Assert.That(FirstRunFlowOutcomes.Agents(new FirstRunFlowResult.Unavailable())).IsNull();
        await Assert.That(FirstRunFlowOutcomes.Agents(new FirstRunFlowResult.Expired())).IsNull();
        await Assert.That(FirstRunFlowOutcomes.Agents(new FirstRunFlowResult.Abandoned(null))).IsNull();
    }
}
