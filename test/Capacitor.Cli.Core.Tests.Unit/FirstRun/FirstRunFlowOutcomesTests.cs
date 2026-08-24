using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

// The boundary between the wire and anything acted on. The payload is effectively executable
// downstream — kcap setup writes Claude Code hooks and a hook entry is a command string Claude Code
// runs — so what these pin is that an unrecognised value is dropped rather than carried.
public class FirstRunFlowOutcomesTests {
    static FirstRunFlowResponse View(bool canFinish, params (string Step, string Status)[] steps) =>
        new() {
            FlowId    = "b7f3a1c2d4e5f607a1b2c3",
            Step      = "Done",
            CanFinish = canFinish,
            Steps     = steps.ToDictionary(s => s.Step, s => s.Status)
        };

    static FirstRunFlowResponse AllSettled(string doneStatus = "Completed") =>
        View(true,
            ("SignIn", "Completed"), ("Agents", "Completed"), ("Import", "Skipped"), ("Done", doneStatus));

    [Test]
    [Arguments("SignIn", FirstRunFlowStep.SignIn)]
    [Arguments("Agents", FirstRunFlowStep.Agents)]
    [Arguments("Import", FirstRunFlowStep.Import)]
    [Arguments("Done",   FirstRunFlowStep.Done)]
    public async Task Maps_the_step_names_the_server_sends(string wire, FirstRunFlowStep expected) =>
        await Assert.That(FirstRunFlowOutcomes.Step(wire)).IsEqualTo(expected);

    [Test]
    [Arguments("Workspace")]  // a step a newer server might invent
    [Arguments("signin")]     // the wire is case-sensitive; the server sends the enum's own name
    [Arguments("1")]
    [Arguments("SignIn,Done")]
    [Arguments("")]
    public async Task Drops_a_step_name_this_build_does_not_know(string wire) =>
        await Assert.That(FirstRunFlowOutcomes.Step(wire)).IsNull();

    [Test]
    [Arguments("Approved")]
    [Arguments("completed")]
    [Arguments("2")]
    public async Task Drops_an_outcome_this_build_does_not_know(string wire) =>
        await Assert.That(FirstRunFlowOutcomes.Outcome(wire)).IsNull();

    [Test]
    public async Task Reads_an_unknown_outcome_as_pending__which_keeps_the_poll_waiting() {
        // The alternative readings are both worse: settled would end the poll on a value this build
        // could not read, and a hard failure would break an old CLI against a newer server for a
        // step it never needed to understand.
        var view = View(true, ("SignIn", "Completed"), ("Done", "Ratified"));

        await Assert.That(FirstRunFlowOutcomes.StatusOf(view, FirstRunFlowStep.Done))
                    .IsEqualTo(FirstRunStepOutcome.Pending);
        await Assert.That(FirstRunFlowOutcomes.IsFinished(view)).IsFalse();
    }

    [Test]
    public async Task Reads_a_missing_step_as_pending() {
        var view = View(true, ("SignIn", "Completed"));

        await Assert.That(FirstRunFlowOutcomes.StatusOf(view, FirstRunFlowStep.Agents))
                    .IsEqualTo(FirstRunStepOutcome.Pending);
    }

    [Test]
    public async Task Reads_a_null_steps_map_as_pending_throughout() {
        var view = new FirstRunFlowResponse { FlowId = "x", Step = "SignIn", CanFinish = true };

        await Assert.That(FirstRunFlowOutcomes.StatusOf(view, FirstRunFlowStep.SignIn))
                    .IsEqualTo(FirstRunStepOutcome.Pending);
        await Assert.That(FirstRunFlowOutcomes.IsFinished(view)).IsFalse();
    }

    [Test]
    [Arguments("Completed")]
    [Arguments("Skipped")]
    [Arguments("Failed")]
    public async Task Is_finished_on_any_settled_outcome_for_a_step_after_the_gate(string doneStatus) {
        // Skipped and failed both count. Nothing after the gate blocks finishing, so a flow whose
        // last step failed is over rather than stuck — and a poll that held out for Completed would
        // wait out its whole budget on one.
        await Assert.That(FirstRunFlowOutcomes.IsFinished(AllSettled(doneStatus))).IsTrue();
    }

    [Test]
    public async Task Is_not_finished_while_a_known_step_is_unsettled() {
        var view = View(true,
            ("SignIn", "Completed"), ("Agents", "Completed"), ("Import", "Active"), ("Done", "Pending"));

        await Assert.That(FirstRunFlowOutcomes.IsFinished(view)).IsFalse();
    }

    [Test]
    public async Task Is_not_finished_when_the_server_says_a_gate_is_outstanding() {
        // Which steps are gates is the server's to say, and can_finish is how it says it. Restating
        // the rule here is what would let an old CLI call a flow finished whose sign-in failed — so
        // this is the test that stops that duplication creeping back in.
        var view = View(false,
            ("SignIn", "Failed"), ("Agents", "Skipped"), ("Import", "Skipped"), ("Done", "Completed"));

        await Assert.That(FirstRunFlowOutcomes.IsFinished(view)).IsFalse();
    }

    [Test]
    public async Task Ignores_a_step_beyond_the_ones_it_knows() {
        // A newer server's extra step must not keep an old CLI polling forever: it stops when the
        // steps it understands are settled, which is the most it can reason about.
        var view = View(true,
            ("SignIn", "Completed"), ("Agents", "Completed"), ("Import", "Skipped"),
            ("Done",   "Completed"), ("Workspace", "Pending"));

        await Assert.That(FirstRunFlowOutcomes.IsFinished(view)).IsTrue();
    }
}
