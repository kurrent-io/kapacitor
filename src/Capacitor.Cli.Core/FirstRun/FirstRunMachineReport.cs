using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// What this machine tells the flow about itself, once, when the CLI creates it.
///
/// <para>Separate from <see cref="HarnessInventory"/> — the daemon's report — because that one ORs the
/// two detection signals into a single flag, which is lossy for a screen that names the signal it
/// saw. See <see cref="FirstRunHarnessReport"/>.</para>
/// </summary>
/// <param name="Machine">The display tag. Not identity; the server truncates it.</param>
/// <param name="MachineId">This machine's id, so its reports correlate. Null means unreported — an
/// empty string would correlate every unreported flow to the same non-machine.</param>
/// <param name="Harnesses">Per vendor, over every harness this build knows. A vendor missing from
/// here reads as unknown rather than absent, so all of them are reported even when both signals are
/// false.</param>
/// <param name="Declined">Vendors turned down locally, outside the flow.</param>
/// <param name="LoginShellFindsCli">Whether the login shell resolves the CLI, or null when nothing
/// probed. <b>Null is not false</b> — see the wire model.</param>
/// <param name="Platform">This machine's platform, or null when it is none the flow names. What decides
/// whether the screen can offer to fix a broken PATH at all — see <see cref="FirstRunPlatforms"/>.</param>
public sealed record FirstRunMachineReport(
        string?                                            Machine,
        string?                                            MachineId,
        IReadOnlyDictionary<string, FirstRunHarnessReport> Harnesses,
        IReadOnlyList<string>                              Declined,
        bool?                                              LoginShellFindsCli,
        string?                                            Platform = null) {
    /// <summary>Vendors this machine reported a signal for. The set the Agents screen could offer,
    /// and so the only set a decision can be read as REFUSING: a vendor with history on disk but
    /// nothing installed now was never offered, and its absence from an answer is not a refusal.</summary>
    public IReadOnlyList<string> Detected =>
        [.. Harnesses.Where(kv => kv.Value.BinaryOnPath || kv.Value.ConfigFound).Select(kv => kv.Key)];

    /// <summary>Pure core: the harnesses this process sees and the ledger already resolved, so the
    /// vendor-to-report mapping is testable without touching the filesystem or PATH.</summary>
    public static FirstRunMachineReport Evaluate(
            string?            machine,
            string?            machineId,
            HarnessRegistry    harnesses,
            HarnessOfferLedger ledger,
            bool?              loginShellFindsCli,
            string?            platform = null) {
        var harnessReports = new Dictionary<string, FirstRunHarnessReport>(StringComparer.Ordinal);
        var declined       = new List<string>();

        foreach (var harness in harnesses) {
            var vendorId = harness.VendorId;
            var agent    = harnesses.Detect(harness.Id);

            harnessReports[vendorId] = new FirstRunHarnessReport {
                BinaryOnPath = agent.BinaryFound,
                ConfigFound  = agent.InstallSignalFound,
                AlreadyWired = harness.Signals.IsWired
            };

            if (ledger.Entry(harness.Id) is { Declined: true }) declined.Add(vendorId);
        }

        return new FirstRunMachineReport(
            Blank(machine)   ? null : machine,
            Blank(machineId) ? null : machineId,
            harnessReports,
            declined,
            loginShellFindsCli,
            platform);
    }

    /// <summary>
    /// Production convenience: the current process environment, the on-disk offer ledger read without
    /// claiming its throttle stamp, and this machine's id.
    ///
    /// <para><b>Never throws.</b> This runs inside the browser leg, where an exception would be
    /// reported as "could not start browser setup" and cost the user the flow entirely.</para>
    ///
    /// <para><b>And reports nothing at all when it fails</b> — not an empty harness map, which the
    /// server cannot tell from a machine that was probed and found bare. The login-shell answer goes
    /// with it, because it is what keeps the server's <c>ReadFacts</c> from recording the block: a
    /// crash rendered on the consent screen as "no coding agents were found" is a failure reported as
    /// a result.</para>
    /// </summary>
    public static FirstRunMachineReport EvaluateCurrent(
            ConfigRoot config, HarnessRegistry harnesses, string? machine, bool? loginShellFindsCli) {
        try {
            return Evaluate(
                machine,
                // The one unguarded read here: it creates and writes a config file, unlike every
                // probe around it, which swallows its own I/O failures.
                MachineIdOrNull(config),
                harnesses,
                new HarnessOfferStore(config).Load(),
                loginShellFindsCli,
                FirstRunPlatforms.Current());
        } catch (Exception) {
            // No platform either, though reading it cannot fail: the server drops a block with no
            // harnesses, no declines and no probe answer, and a lone platform must not be what keeps it.
            return new FirstRunMachineReport(machine, null, new Dictionary<string, FirstRunHarnessReport>(), [], null);
        }
    }

    static string? MachineIdOrNull(ConfigRoot config) {
        try {
            return new Core.MachineId(config).Get();
        } catch (Exception) {
            return null;
        }
    }

    static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
