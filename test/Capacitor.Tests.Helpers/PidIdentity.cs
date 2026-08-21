using System.Globalization;
using Capacitor.Cli.Core;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Asserts on a process by the identity of its <i>incarnation</i>, not by its pid number, so a pid
/// the OS has reassigned reads as gone instead of as still-alive.
/// </summary>
/// <remarks>
/// <para>
/// A pid you no longer own — a reaped child, a grandchild reparented to init — can be reassigned at
/// any moment, and <c>kill(pid, 0)</c> is then wrong in both directions: a death assertion sees the
/// squatter and fails, and a liveness assertion sees the squatter and passes though the process it
/// meant died. <see cref="ProcessStartToken"/> is production's answer to exactly that, and this is
/// the same primitive with a test-shaped surface.
/// </para>
/// <para>
/// Reaping a child you forked and have not yet reaped needs none of this: the zombie holds its pid
/// until you wait on it, so the number cannot be reassigned in between.
/// </para>
/// </remarks>
public static class PidIdentity {
    /// <summary>
    /// The incarnation token for a pid that must be alive now. Throws rather than returning null:
    /// arming a watch on an unreadable identity would silently degrade to the pid-only check this
    /// type exists to replace, and a successful capture is what makes a later null mean "left the
    /// process table" rather than "we never could read it".
    /// </summary>
    public static string Capture(int pid) =>
        ProcessStartToken.ForPid(pid)
     ?? throw new InvalidOperationException(
            $"No start identity for pid {pid}: it is already gone, or its identity is unreadable "
          + "from here. Capture while the process is provably alive.");

    /// <summary>
    /// True once <paramref name="pid"/> no longer carries <paramref name="identity"/>: it left the
    /// process table, or the number belongs to a different incarnation. A Unix zombie is NOT gone —
    /// its identity stays readable until someone reaps it, which is what makes "the shim reaped it"
    /// directly assertable.
    /// </summary>
    public static bool IsGone(int pid, string identity) => ProcessStartToken.Matches(pid, identity) != true;

    /// <summary>Polls <see cref="IsGone"/> to a deadline; throws naming the pid, identity and elapsed time.</summary>
    public static async Task WaitUntilGoneAsync(int pid, string identity, TimeSpan timeout) {
        var start = Environment.TickCount64;

        while (!IsGone(pid, identity)) {
            if (Environment.TickCount64 - start >= (long) timeout.TotalMilliseconds)
                throw new TimeoutException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"pid {pid} still carries identity '{identity}' after {timeout.TotalSeconds:0.#}s."));

            await Task.Delay(50);
        }
    }
}
