namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// Fails the RUN if any live cert left a nonce memory it could not confirm was removed.
///
/// <para><b>Why this exists as well as the gate in <c>SkipUnlessLiveGateReady</c>.</b> That gate is
/// prospective: it stops the NEXT case from starting against a possibly-dirty index. But if the
/// cleanup failure happens in the last case — or the only one — nothing ever looks at the flag, and a
/// run that leaked a nonce reports green. A leaked nonce makes a LATER run's positive case pass on
/// this run's evidence, so it has to be a failure now, not a warning nobody reads.</para>
///
/// <para>Costs nothing when no live cert ran: the flag can only be set by the gated cert path, so in
/// CI this hook observes null and returns.</para>
/// </summary>
public class LiveCertIndexCleanlinessGuard {
    [After(Assembly)]
    public static void FailIfALiveCertLeftTheIndexDirty() {
        if (MemoryIndexLiveCertHarness.IndexCleanlinessUnconfirmed is not { } reason) return;

        throw new InvalidOperationException(
            "A live memory cert could not confirm its nonce memory was removed, so the real injected "
          + "index may now carry a stale nonce — which would make a later run's positive case pass on "
          + $"this run's evidence. Find and archive it before certifying anything again. Reason: {reason}");
    }
}
