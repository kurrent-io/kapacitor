using Capacitor.Cli.Core.Config;
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

    static readonly DateTimeOffset Asked = new(2026, 8, 21, 12, 5, 0, TimeSpan.Zero);

    static FirstRunFlowResponse Asking(params FirstRunMachineActionResponse[] actions) =>
        AllSettled() with { MachineActions = [.. actions] };

    static FirstRunMachineActionResponse Action(string capability, DateTimeOffset? requestedAt) =>
        new() { Capability = capability, RequestedAt = requestedAt };

    [Test]
    public async Task Reads_a_request_this_build_can_name() {
        var requested = FirstRunFlowOutcomes.MachineActions(
            Asking(Action(FirstRunMachineCapabilities.PathShim, Asked)));

        await Assert.That(requested.Count).IsEqualTo(1);
        await Assert.That(requested[0].Capability).IsEqualTo(FirstRunMachineCapabilities.PathShim);
        await Assert.That(requested[0].RequestedAt).IsEqualTo(Asked);
    }

    [Test]
    [Arguments("reboot_the_laptop")]
    [Arguments("PATH_SHIM")]
    [Arguments("")]
    public async Task Drops_a_capability_this_build_cannot_name(string capability) =>
        await Assert.That(FirstRunFlowOutcomes.MachineActions(Asking(Action(capability, Asked)))).IsEmpty();

    [Test]
    public async Task Drops_a_request_with_no_timestamp() {
        // The outcome is reported against it, so an unidentifiable request cannot be answered — and
        // performing it anyway would raise an admin prompt whose result had nowhere to go.
        var requested = FirstRunFlowOutcomes.MachineActions(
            Asking(Action(FirstRunMachineCapabilities.PathShim, null)));

        await Assert.That(requested).IsEmpty();
    }

    [Test]
    public async Task Keeps_the_first_of_a_capability_named_twice() {
        // Prompts once. The server folds one request per capability, so this only guards a hand-made body.
        var requested = FirstRunFlowOutcomes.MachineActions(Asking(
            Action(FirstRunMachineCapabilities.PathShim, Asked),
            Action(FirstRunMachineCapabilities.PathShim, Asked.AddMinutes(1))));

        await Assert.That(requested.Count).IsEqualTo(1);
        await Assert.That(requested[0].RequestedAt).IsEqualTo(Asked);
    }

    [Test]
    public async Task Reads_an_absent_or_empty_action_list_as_nothing_outstanding() {
        await Assert.That(FirstRunFlowOutcomes.MachineActions(AllSettled())).IsEmpty();
        await Assert.That(FirstRunFlowOutcomes.MachineActions(Asking())).IsEmpty();
        await Assert.That(FirstRunFlowOutcomes.MachineActions(null)).IsEmpty();
    }

    // =====================================================================
    // The Import decision, mapped through the same closed sets.
    // =====================================================================

    static readonly DateTimeOffset ImportDecided = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    static FirstRunFlowResponse WithImport(
            FirstRunImportDecisionResponse? decision, DateTimeOffset? decidedAt = null) =>
        AllSettled() with { Import = decision, ImportDecidedAt = decision is null ? null : decidedAt ?? ImportDecided };

    static FirstRunImportDecisionResponse Decision(
            string window = "90", string titles = "Server", params (string Owner, string Name, string Level)[] repos) =>
        new() {
            Window = window,
            Titles = titles,
            Repos  = [.. repos.Select(r => new FirstRunImportRepoChoiceResponse {
                Owner = r.Owner, Name = r.Name, Level = r.Level
            })]
        };

    [Test]
    public async Task No_import_decision_is_unanswered_rather_than_import_nothing() {
        await Assert.That(FirstRunFlowOutcomes.Import(WithImport(null))).IsNull();
    }

    [Test]
    public async Task An_empty_repo_list_is_an_answer() {
        var answer = FirstRunFlowOutcomes.Import(WithImport(Decision()));

        await Assert.That(answer).IsNotNull();
        await Assert.That(answer!.IsDecline).IsTrue();
    }

    [Test]
    public async Task A_decision_with_no_timestamp_has_no_identity_and_is_not_read() {
        // Reading half of it would act on a choice with nothing to compare against.
        var view = AllSettled() with { Import = Decision(), ImportDecidedAt = null };

        await Assert.That(FirstRunFlowOutcomes.Import(view)).IsNull();
    }

    [Test]
    public async Task An_unknown_window_voids_the_whole_decision() {
        // It names what to do with everything selected, and there is no safe guess: narrower silently
        // skips history, wider silently uploads more than was asked for.
        var view = WithImport(Decision(window: "365", repos: ("kurrent-io", "kcap", "Shared")));

        await Assert.That(FirstRunFlowOutcomes.Import(view)).IsNull();
    }

    [Test]
    public async Task An_unknown_titles_answer_voids_the_whole_decision() {
        var view = WithImport(Decision(titles: "Telepathy", repos: ("kurrent-io", "kcap", "Shared")));

        await Assert.That(FirstRunFlowOutcomes.Import(view)).IsNull();
    }

    [Test]
    public async Task An_unknown_level_costs_that_repository_and_nothing_else() {
        // The rest of the answer still applies: an old CLI meeting one new stop should import the
        // repositories it does understand, and say how many it could not.
        var answer = FirstRunFlowOutcomes.Import(WithImport(Decision(
            repos: [("kurrent-io", "kcap", "Shared"), ("kurrent-io", "other", "Telemetry")])))!;

        await Assert.That(answer.Choices.Single().Name).IsEqualTo("kcap");
        await Assert.That(answer.Unreadable).IsEqualTo(1);
        await Assert.That(answer.IsDecline).IsFalse().Because("something was asked for, it just could not be read");
    }

    [Test]
    public async Task A_repository_named_twice_is_imported_once_at_the_first_level_given() {
        // Case-insensitively, because git remotes are: two spellings would import it twice, at
        // whichever level came second.
        var answer = FirstRunFlowOutcomes.Import(WithImport(Decision(
            repos: [("kurrent-io", "kcap", "OnlyMe"), ("KURRENT-IO", "KCAP", "Shared")])))!;

        await Assert.That(answer.Choices.Single().Level).IsEqualTo(FirstRunImportLevel.OnlyMe);
    }

    [Test]
    public async Task A_repository_with_half_an_identity_is_dropped_without_being_counted() {
        // Not "a repository we could not read" — it is not a repository, so counting it would have the
        // user chase one that was never there.
        var answer = FirstRunFlowOutcomes.Import(WithImport(Decision(
            repos: [("kurrent-io", "", "Shared")])))!;

        await Assert.That(answer.Choices).IsEmpty();
        await Assert.That(answer.Unreadable).IsEqualTo(0);
    }

    [Test]
    public async Task Null_vendors_stay_null_because_no_filter_is_not_filter_to_nothing() {
        var answer = FirstRunFlowOutcomes.Import(WithImport(Decision(repos: ("o", "n", "Shared"))))!;

        await Assert.That(answer.Vendors).IsNull();
    }

    [Test]
    public async Task An_empty_vendor_list_stays_empty() {
        var view = WithImport(Decision(repos: ("o", "n", "Shared")) with { Vendors = [] });

        await Assert.That(FirstRunFlowOutcomes.Import(view)!.Vendors).IsNotNull().And.IsEmpty();
    }

    [Test]
    public async Task A_vendor_this_build_does_not_know_is_dropped_from_the_filter() {
        var view = WithImport(Decision(repos: ("o", "n", "Shared")) with { Vendors = ["claude", "telepathy"] });

        await Assert.That(FirstRunFlowOutcomes.Import(view)!.Vendors).IsEquivalentTo(["claude"]);
    }

    [Test]
    public async Task Levels_split_into_the_passes_that_run_them() {
        // --private is per invocation, so a level is a pass rather than a per-repo flag.
        var answer = FirstRunFlowOutcomes.Import(WithImport(Decision(
            repos: [("o", "mine", "OnlyMe"), ("o", "ours", "Shared"), ("o", "also-mine", "OnlyMe")])))!;

        await Assert.That(answer.At(FirstRunImportLevel.OnlyMe).Select(c => c.Name))
                    .IsEquivalentTo(["mine", "also-mine"]);
        await Assert.That(answer.At(FirstRunImportLevel.Shared).Single().Name).IsEqualTo("ours");
    }

    [Test]
    public async Task Only_local_titling_keeps_the_title_pass_on_this_machine() {
        // Server-side and nobody differ in what happens on the server, not in what this machine does.
        var server = FirstRunFlowOutcomes.Import(WithImport(Decision(titles: "Server", repos: ("o", "n", "Shared"))))!;
        var local  = FirstRunFlowOutcomes.Import(WithImport(Decision(titles: "Local",  repos: ("o", "n", "Shared"))))!;
        var none   = FirstRunFlowOutcomes.Import(WithImport(Decision(titles: "None",   repos: ("o", "n", "Shared"))))!;

        await Assert.That(server.SkipTitle).IsTrue();
        await Assert.That(local.SkipTitle).IsFalse();
        await Assert.That(none.SkipTitle).IsTrue();
    }

    [Test]
    public async Task The_window_resolves_to_a_since_date_on_this_machines_clock() {
        var today  = new DateOnly(2026, 6, 15);
        var answer = FirstRunFlowOutcomes.Import(WithImport(Decision(window: "30", repos: ("o", "n", "Shared"))))!;

        await Assert.That(answer.Since(today)).IsEqualTo(new DateOnly(2026, 5, 16));
    }

    [Test]
    public async Task Everything_resolves_to_no_since_at_all() {
        // Not a very old date: --since with no value is what "everything" means to the import.
        var answer = FirstRunFlowOutcomes.Import(WithImport(Decision(window: "all", repos: ("o", "n", "Shared"))))!;

        await Assert.That(answer.Since(new DateOnly(2026, 6, 15))).IsNull();
    }

    [Test]
    public async Task An_unsettled_import_step_carries_no_decision_to_act_on() {
        // The decision and the step's outcome are separate fields, so a view can carry choices for a
        // step still being answered.
        var view = View(true,
            ("SignIn", "Completed"), ("Agents", "Completed"), ("Import", "Active"), ("Done", "Pending"));

        var result = new FirstRunFlowResult.Abandoned(
            view with { Import = Decision(repos: ("o", "n", "Shared")), ImportDecidedAt = ImportDecided });

        await Assert.That(FirstRunFlowOutcomes.Import(result)).IsNull();
    }

    [Test]
    public async Task A_dismissed_leg_still_carries_the_decision_it_was_given() {
        // The user answered the screen and then closed the tab; the answer was still theirs.
        var result = new FirstRunFlowResult.Dismissed(WithImport(Decision(repos: ("o", "n", "Shared"))));

        await Assert.That(FirstRunFlowOutcomes.Import(result)).IsNotNull();
    }

    static FirstRunFlowResponse WithVisibility(string? visibility) =>
        AllSettled() with {
            Agents          = [new FirstRunAgentChoiceResponse { Vendor = "claude", Record = true, Tools = true }],
            AgentsDecidedAt = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
            DefaultVisibility = visibility
        };

    [Test]
    public async Task Each_stop_the_wire_can_name_is_carried_through() {
        foreach (var stop in AppConfig.ValidVisibilities) {
            await Assert.That(FirstRunFlowOutcomes.Agents(WithVisibility(stop))!.DefaultVisibility)
                        .IsEqualTo(stop);
        }
    }

    [Test]
    public async Task No_visibility_answer_leaves_the_profile_alone() {
        // Null covers both "unanswered" and "declined everything", and neither asks for a default.
        await Assert.That(FirstRunFlowOutcomes.Agents(WithVisibility(null))!.DefaultVisibility).IsNull();
    }

    [Test]
    public async Task A_stop_this_build_does_not_know_is_dropped_rather_than_written_to_disk() {
        // It would land in profile config and be stamped on every session afterwards, so forwarding one
        // a newer server invented writes a value this build cannot reason about to a file it owns.
        await Assert.That(FirstRunFlowOutcomes.Agents(WithVisibility("telepathy"))!.DefaultVisibility).IsNull();
    }

    [Test]
    public async Task An_empty_visibility_string_is_not_a_stop() {
        await Assert.That(FirstRunFlowOutcomes.Agents(WithVisibility(""))!.DefaultVisibility).IsNull();
    }

    [Test]
    public async Task Declining_every_harness_still_carries_the_visibility_answer() {
        // Two separate questions on one screen: installing nothing and choosing who may read future
        // sessions are both coherent together.
        var view = AllSettled() with {
            Agents            = [],
            AgentsDecidedAt   = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
            DefaultVisibility = "private"
        };

        var answer = FirstRunFlowOutcomes.Agents(view)!;

        await Assert.That(answer.IsDecline).IsTrue();
        await Assert.That(answer.DefaultVisibility).IsEqualTo("private");
    }
}
