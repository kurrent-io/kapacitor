namespace Capacitor.Cli.Core.FirstRun;

/// <summary>What the browser leg shows a human: a URL and a wait. There is no code to compare or
/// enter — the CLI authenticates itself, so the flow has no second party for one.</summary>
public interface IFirstRunFlowProgress {
    /// <summary>The browser is being handed <paramref name="setupUrl"/>. Printed as well as opened,
    /// because a machine that cannot open one is exactly the machine whose user needs to read it.</summary>
    void Opening(string setupUrl);

    /// <summary>
    /// One poll came back with the flow still running, on <paramref name="flowStep"/> — null where the
    /// server named a step this build has never heard of.
    ///
    /// <para><b><paramref name="healthy"/> is what the poll itself did, not what the flow is doing.</b>
    /// False means no state came back at all, so the step is the last one known rather than the current
    /// one: naming a screen the user is supposedly looking at, while the server has been silent for
    /// minutes, states a fact nothing here has.</para>
    /// </summary>
    void Waiting(FirstRunFlowStep? flowStep, bool healthy);

    /// <summary>
    /// <paramref name="flowStep"/> has an outcome, said once per step.
    ///
    /// <para><paramref name="detail"/> names the harnesses the Agents answer chose, and is null
    /// everywhere else — the wire carries nothing else worth repeating back, and a step's own copy is
    /// the host's to write.</para>
    /// </summary>
    void Settled(FirstRunFlowStep flowStep, FirstRunStepOutcome outcome, string? detail);

    /// <summary>
    /// The browser asked this machine to perform <paramref name="capability"/>, and it is about to run.
    ///
    /// <para><b>Said before it runs, not after.</b> The PATH shim raises an admin-password dialog, and a
    /// password prompt the user was not warned about — while they are looking at a browser — is
    /// indistinguishable from malware.</para>
    ///
    /// <para><b>Some capabilities write to the console while they run</b> — the service ladder emits its
    /// coded tokens — so a host rendering the wait inline has to close it here, as it does for the
    /// import, and reopen at <see cref="ActionEnded"/>.</para>
    /// </summary>
    void PerformingAction(string capability);

    /// <summary>The action finished and polling resumes, so an inline wait has to reopen.</summary>
    void ActionEnded();

    /// <summary>Discovery is about to scan this machine for importable history. Said out loud
    /// because it is the one pause long enough to read as a hang, and the screen is waiting on it.</summary>
    void Discovering();

    /// <summary>The import is about to run over <paramref name="repos"/> repositories, holding
    /// <paramref name="sessions"/> sessions where that is known — null where a selected repository
    /// reported no count for the chosen window, since a total that quietly omitted one would be the
    /// wrong number stated confidently. Its own output follows, so a host rendering the wait inline
    /// has to close it here rather than at <see cref="WaitEnded"/>.</summary>
    void Importing(int repos, int? sessions);

    /// <summary>The import finished and polling resumes, so an inline wait has to reopen.</summary>
    void ImportEnded();

    /// <summary>The wait is over, however it ended. A host that rendered the wait inline needs this to
    /// close it; one that did not can ignore it.</summary>
    void WaitEnded();
}
