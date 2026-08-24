namespace Capacitor.Cli.Core.FirstRun;

/// <summary>What the browser leg shows a human: a URL and a wait. There is no code to compare or
/// enter — the CLI authenticates itself, so the flow has no second party for one.</summary>
public interface IFirstRunFlowProgress {
    /// <summary>The browser is being handed <paramref name="setupUrl"/>. Printed as well as opened,
    /// because a machine that cannot open one is exactly the machine whose user needs to read it.</summary>
    void Opening(string setupUrl);

    /// <summary>One poll came back with the flow still running.</summary>
    void PollTick();

    /// <summary>The wait is over, however it ended. A host that rendered the wait inline needs this to
    /// close it; one that did not can ignore it.</summary>
    void WaitEnded();
}
