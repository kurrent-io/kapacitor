using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.FirstRun;

/// <summary>The steps this build knows, in flow order. A closed set: a step a newer server invents is
/// not one this CLI can act on, so it has no member here and is dropped on the way in.</summary>
public enum FirstRunFlowStep {
    /// <summary>The gate, and no screen of ours — the tenant's identity provider hosts it.</summary>
    SignIn,

    /// <summary>Detected harnesses to hooks and MCP, and where the flow's consent lives.</summary>
    Agents,

    /// <summary>Backfill of past sessions.</summary>
    Import,

    /// <summary>The payoff screen.</summary>
    Done
}

/// <summary>How one step ended. Mirrors the server's vocabulary, and nothing wider.</summary>
public enum FirstRunStepOutcome {
    /// <summary>Never entered.</summary>
    Pending,

    /// <summary>Entered, no outcome yet.</summary>
    Active,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Declined, or not applicable to this machine.</summary>
    Skipped,

    /// <summary>Attempted and failed. Not fatal to the flow — nothing after the gate blocks finishing.</summary>
    Failed
}

/// <summary>
/// The boundary between the wire and anything this CLI acts on.
///
/// <para><b>Unknown members are dropped, never forwarded.</b> An old CLI meeting a value a new server
/// invented must not pass it on to whatever consumes it — the payload is effectively executable
/// downstream, since <c>kcap setup</c> writes Claude Code hooks and a hook entry is a command string
/// Claude Code runs. Mapping through a closed set is what makes <b>values, never paths, file bodies or
/// command strings</b> enforceable rather than merely intended.</para>
///
/// <para>Written as switches rather than <c>Enum.TryParse</c> deliberately: parsing by reflection
/// accepts numeric strings and comma-separated combinations, neither of which the server sends, and
/// both of which would widen the set this exists to close.</para>
/// </summary>
public static class FirstRunFlowOutcomes {
    /// <summary>Flow order, and the set of steps this build can reason about.</summary>
    public static IReadOnlyList<FirstRunFlowStep> KnownSteps { get; } = [
        FirstRunFlowStep.SignIn,
        FirstRunFlowStep.Agents,
        FirstRunFlowStep.Import,
        FirstRunFlowStep.Done
    ];

    /// <summary>The step a wire name means, or null when this build has never heard of it.</summary>
    public static FirstRunFlowStep? Step(string? name) => name switch {
        "SignIn" => FirstRunFlowStep.SignIn,
        "Agents" => FirstRunFlowStep.Agents,
        "Import" => FirstRunFlowStep.Import,
        "Done"   => FirstRunFlowStep.Done,
        _        => null
    };

    /// <summary>The outcome a wire name means, or null when this build has never heard of it.</summary>
    public static FirstRunStepOutcome? Outcome(string? name) => name switch {
        "Pending"   => FirstRunStepOutcome.Pending,
        "Active"    => FirstRunStepOutcome.Active,
        "Completed" => FirstRunStepOutcome.Completed,
        "Skipped"   => FirstRunStepOutcome.Skipped,
        "Failed"    => FirstRunStepOutcome.Failed,
        _           => null
    };

    /// <summary>How <paramref name="step"/> ended, as far as this build can tell. A step the response
    /// omits, and one whose outcome this build does not recognise, are both
    /// <see cref="FirstRunStepOutcome.Pending"/> — the reading that keeps the CLI waiting rather than
    /// declaring a flow over on a value it could not read.</summary>
    public static FirstRunStepOutcome StatusOf(FirstRunFlowResponse view, FirstRunFlowStep step) {
        if (view.Steps is not { } steps) return FirstRunStepOutcome.Pending;

        return steps.TryGetValue(step.ToString(), out var raw)
            ? Outcome(raw) ?? FirstRunStepOutcome.Pending
            : FirstRunStepOutcome.Pending;
    }

    /// <summary>Whether a step has an outcome at all, of any kind.</summary>
    public static bool IsSettled(FirstRunFlowResponse view, FirstRunFlowStep step) =>
        StatusOf(view, step) is FirstRunStepOutcome.Completed
                             or FirstRunStepOutcome.Skipped
                             or FirstRunStepOutcome.Failed;

    /// <summary>
    /// Whether the poll can stop: every gate has completed, and every step this build knows has an
    /// outcome.
    ///
    /// <para><b>Which steps are gates is the server's to say, not this file's.</b> That is what
    /// <c>can_finish</c> carries, and taking it from there rather than restating the rule locally is
    /// what stops an old CLI calling a flow finished whose sign-in failed. The settled test below is
    /// deliberately the permissive one — skipped and failed both count — because nothing after the
    /// gate blocks finishing, and a flow whose import failed is over, not stuck.</para>
    /// </summary>
    public static bool IsFinished(FirstRunFlowResponse view) =>
        view.CanFinish && KnownSteps.All(step => IsSettled(view, step));

    /// <summary>
    /// The Agents decision, or null when there is none to act on.
    ///
    /// <para><b>A vendor key this build does not know is dropped, never forwarded.</b> The rest of the
    /// answer still applies: an old CLI meeting one new vendor should set up the eight it does know.</para>
    ///
    /// <para><b>Both wire fields or neither.</b> A response carrying choices with no timestamp — or the
    /// reverse — is one this build cannot read, and reading half of it would apply a decision with no
    /// identity. Treated as unanswered, which is the reading that changes nothing.</para>
    /// </summary>
    public static FirstRunAgentsAnswer? Agents(FirstRunFlowResponse? view) {
        if (view?.Agents is not { } entries) return null;
        if (view.AgentsDecidedAt is not { } decidedAt) return null;

        var seen         = new HashSet<HarnessId>();
        var choices      = new List<FirstRunAgentsChoice>();
        var unrecognised = 0;

        foreach (var entry in entries) {
            if (HarnessId.From(entry.Vendor) is not { } harness) {
                unrecognised++;

                continue;
            }

            // "Neither" is how a harness was left alone. The server normalises these out; dropping
            // them here too keeps an answer that asks for nothing indistinguishable from a decline,
            // which is what it is.
            //
            // Tested BEFORE the duplicate guard, or a leading neither-entry claims the vendor's slot
            // and the real choice behind it is dropped as the duplicate — leaving nothing.
            if (!entry.Record && !entry.Tools) continue;

            // A vendor named twice cannot happen against the server this was written for, which
            // validates on the way in. Keeping the first is the reading that installs what was asked
            // for once rather than picking a side between two contradictory entries.
            if (!seen.Add(harness)) continue;

            choices.Add(new FirstRunAgentsChoice(harness, entry.Record, entry.Tools));
        }

        return new FirstRunAgentsAnswer(choices, decidedAt, unrecognised, Visibility(view.DefaultVisibility));
    }

    /// <summary>
    /// The default visibility to write, or null to leave the profile alone.
    ///
    /// <para><b>Validated against this build's own closed set, not forwarded.</b> The value lands in
    /// profile config and is stamped on every session this machine records afterwards, so one a newer
    /// server invented would be written to disk and then read back by a server whose meaning for it this
    /// build cannot check. Null is what a dropped value degrades to, which leaves the profile untouched —
    /// the same outcome as never having asked.</para>
    /// </summary>
    static string? Visibility(string? value) =>
        value is { Length: > 0 } && AppConfig.ValidVisibilities.Contains(value) ? value : null;

    /// <summary>
    /// The actions the browser is asking this machine to perform, filtered to ones this build can name.
    ///
    /// <para><b>An unknown capability is dropped, never forwarded.</b> Same rule as the vendor keys, and
    /// the stakes are higher: this is the one field on the response the CLI acts on rather than records,
    /// so mapping through a closed set is what keeps "a named capability" from degrading into "whatever
    /// the server said".</para>
    ///
    /// <para><b>A request with no timestamp is dropped too.</b> The outcome is reported against it, so
    /// one that cannot be identified cannot be answered — and performing it anyway would raise an admin
    /// prompt whose result had nowhere to go.</para>
    /// </summary>
    public static IReadOnlyList<FirstRunMachineActionRequest> MachineActions(FirstRunFlowResponse? view) {
        if (view?.MachineActions is not { } entries) return [];

        var seen      = new HashSet<string>(StringComparer.Ordinal);
        var requested = new List<FirstRunMachineActionRequest>();

        foreach (var entry in entries) {
            if (!FirstRunMachineCapabilities.IsKnown(entry.Capability)) continue;
            if (entry.RequestedAt is not { } requestedAt) continue;

            // A capability named twice cannot happen against the server this was written for, which folds
            // one request per capability. Keeping the first is the reading that prompts once.
            if (!seen.Add(entry.Capability)) continue;

            requested.Add(new FirstRunMachineActionRequest(entry.Capability, requestedAt));
        }

        return requested;
    }

    /// <summary>
    /// The vendors to scan for importable history: every one this build knows except what this
    /// machine offered and the user did not keep.
    ///
    /// <para><b>Only an explicit refusal drops a vendor.</b> The server normalises a harness nothing
    /// was turned on for out of the decision, so refused and never-offered look identical on the wire
    /// — but this machine knows which ones it reported, and that is the difference. Getting it wrong
    /// the other way silently discards history for an agent the user was never asked about.</para>
    ///
    /// <para>An unanswered step scans everything: no answer is not a refusal either.</para>
    /// </summary>
    public static IReadOnlyList<HarnessId> VendorsToImportFrom(
            FirstRunMachineReport report, FirstRunAgentsAnswer? agents) {
        var all = HarnessRegistry.Identities.Select(h => h.Id);

        if (agents is null) return [.. all];

        var refused = new HashSet<HarnessId>();

        foreach (var vendorId in report.Detected) {
            if (HarnessId.From(vendorId) is { } harness && !agents.Records(harness)) refused.Add(harness);
        }

        return [.. all.Where(id => !refused.Contains(id))];
    }

    /// <summary>The level a wire name means, or null when this build has never heard of it.</summary>
    public static FirstRunImportLevel? Level(string? name) => name switch {
        "OnlyMe" => FirstRunImportLevel.OnlyMe,
        "Shared" => FirstRunImportLevel.Shared,
        _        => null
    };

    /// <summary>Who titles, or null when this build has never heard of the answer.</summary>
    public static FirstRunImportTitles? Titles(string? name) => name switch {
        "Server" => FirstRunImportTitles.Server,
        "Local"  => FirstRunImportTitles.Local,
        "None"   => FirstRunImportTitles.None,
        _        => null
    };

    /// <summary>
    /// The Import decision, or null when there is none to act on.
    ///
    /// <para><b>An unreadable window or titles answer voids the whole decision</b>, unlike an
    /// unreadable level, which costs one repository. Both name what to do with everything selected,
    /// so guessing either would import the right repositories on the wrong terms — and there is no
    /// safe guess: assuming a narrower window silently skips history, a wider one silently uploads
    /// more than was asked for.</para>
    ///
    /// <para><b>Both wire fields or neither</b>, as the Agents decision: a decision with no timestamp
    /// has no identity, and reading half of it would act on a half-made choice.</para>
    /// </summary>
    public static FirstRunImportAnswer? Import(FirstRunFlowResponse? view) {
        if (view?.Import is not { } decision) return null;
        if (view.ImportDecidedAt is not { } decidedAt) return null;
        if (!FirstRunImportWindows.IsKnown(decision.Window)) return null;
        if (Titles(decision.Titles) is not { } titles) return null;

        var seen        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var choices     = new List<FirstRunImportChoice>();
        var unreadable  = 0;

        foreach (var entry in decision.Repos ?? []) {
            if (entry.Owner is not { Length: > 0 } || entry.Name is not { Length: > 0 }) continue;

            if (Level(entry.Level) is not { } level) {
                unreadable++;

                continue;
            }

            // Case-insensitively, because git remotes are: two spellings of one repository would
            // import it twice, at whichever level came second.
            if (!seen.Add($"{entry.Owner}/{entry.Name}")) continue;

            choices.Add(new FirstRunImportChoice(entry.Owner, entry.Name, level));
        }

        return new FirstRunImportAnswer(
            choices, decision.Window, titles, Vendors(decision.Vendors), decidedAt, unreadable);
    }

    /// <summary>The vendors to filter on. <b>Null survives as null</b> — no filter, not filter-to-
    /// nothing — and a vendor this build does not know is dropped, which can turn a non-empty list
    /// into an empty one: importing from nothing beats importing from everything on an answer that
    /// named neither.</summary>
    static IReadOnlyList<HarnessId>? Vendors(List<string>? vendors) =>
        vendors is null ? null : [.. HarnessRegistry.Identities.Where(h => vendors.Contains(h.VendorId)).Select(h => h.Id)];

    /// <summary>The Import decision behind a finished leg, on the same terms as
    /// <see cref="Agents(FirstRunFlowResult)"/>: only from a view whose Import step has settled, and
    /// a dismissed or abandoned leg can carry one — the user answered the screen and then stopped
    /// watching.</summary>
    public static FirstRunImportAnswer? Import(FirstRunFlowResult result) =>
        ViewOf(result) is { } view && IsSettled(view, FirstRunFlowStep.Import) ? Import(view) : null;

    /// <summary>The Agents decision behind a finished leg, wherever it ended.
    ///
    /// <para>A dismissed or abandoned leg can carry one: the user answered the screen and then closed
    /// the tab, or stopped waiting. Consent was given, so there is work to do — the browser settles the
    /// step on the decision being recorded, not on the install finishing.</para>
    ///
    /// <para><b>Only from a view whose Agents step has settled.</b> The decision and the step's outcome
    /// are separate fields, so a view can carry choices for a step still being answered; acting on
    /// those would apply a half-made choice. This is the last state polled, so it can still lag the
    /// server by one interval — the CLI applies what it last saw and does not treat
    /// <see cref="FirstRunAgentsAnswer.DecidedAt"/> as a cursor to re-check against.</para></summary>
    public static FirstRunAgentsAnswer? Agents(FirstRunFlowResult result) =>
        ViewOf(result) is { } view && IsSettled(view, FirstRunFlowStep.Agents) ? Agents(view) : null;

    /// <summary>The last state polled, for a leg that reached one. Null for a leg that never got a
    /// state to report — an unavailable tenant, a rate limit, a create that failed.</summary>
    static FirstRunFlowResponse? ViewOf(FirstRunFlowResult result) => result switch {
        FirstRunFlowResult.Finished finished    => finished.View,
        FirstRunFlowResult.Dismissed dismissed  => dismissed.View,
        FirstRunFlowResult.Abandoned abandoned  => abandoned.View,
        _                                       => null
    };
}
