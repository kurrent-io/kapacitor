namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// What the pairing leg shows a human.
///
/// <para>Separate from <see cref="IAuthProgress.DeviceCode"/>, which looks structurally identical —
/// a code plus a URL — but means the opposite. A device code is <b>entered</b>; a pairing code is
/// <b>displayed and compared, never submitted</b>. Rendering one through the other would produce
/// "enter this code" copy for a value the user must only check, which is the exact confusion the
/// comparison exists to prevent.</para>
/// </summary>
public interface IPairingProgress {
    void AwaitingApproval(string userCode, string setupUrl);

    /// <summary>One poll came back still pending.</summary>
    void PollTick();

    /// <summary>The wait is over, however it ended. A host that rendered the wait inline needs this
    /// to close it; one that did not can ignore it.</summary>
    void WaitEnded();
}
