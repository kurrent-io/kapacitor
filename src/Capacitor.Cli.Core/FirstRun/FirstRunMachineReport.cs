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
/// <param name="Harnesses">Per vendor, over the whole catalogue. A vendor missing from here reads as
/// unknown rather than absent, so every catalogue entry is reported even when both signals are false.</param>
/// <param name="Declined">Vendors turned down locally, outside the flow.</param>
/// <param name="LoginShellFindsCli">Whether the login shell resolves the CLI, or null when nothing
/// probed. <b>Null is not false</b> — see the wire model.</param>
public sealed record FirstRunMachineReport(
        string?                                            Machine,
        string?                                            MachineId,
        IReadOnlyDictionary<string, FirstRunHarnessReport> Harnesses,
        IReadOnlyList<string>                              Declined,
        bool?                                              LoginShellFindsCli) {
    /// <summary>Injectable core: detection, the wired-probe and the ledger already resolved, so the
    /// vendor→report mapping is testable without touching the filesystem or PATH.</summary>
    public static FirstRunMachineReport Evaluate(
            string?              machine,
            string?              machineId,
            AgentDetectionResult detected,
            Func<string, bool>   isWired,
            HarnessOfferLedger   ledger,
            bool?                loginShellFindsCli) {
        var harnesses = new Dictionary<string, FirstRunHarnessReport>(StringComparer.Ordinal);
        var declined  = new List<string>();

        foreach (var harness in HarnessCatalog.All) {
            var agent = harness.Select(detected);

            harnesses[harness.VendorId] = new FirstRunHarnessReport {
                BinaryOnPath = agent.BinaryFound,
                ConfigFound  = agent.InstallSignalFound,
                AlreadyWired = isWired(harness.VendorId)
            };

            if (ledger.Entry(harness.VendorId) is { Declined: true }) declined.Add(harness.VendorId);
        }

        return new FirstRunMachineReport(
            Blank(machine)   ? null : machine,
            Blank(machineId) ? null : machineId,
            harnesses,
            declined,
            loginShellFindsCli);
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
    public static FirstRunMachineReport EvaluateCurrent(ConfigRoot config, string? machine, bool? loginShellFindsCli) {
        try {
            var inputs = AgentDetection.FromEnvironment();

            return Evaluate(
                machine,
                // The one unguarded read here: it creates and writes a config file, unlike every
                // probe around it, which swallows its own I/O failures.
                MachineIdOrNull(config),
                AgentDetection.Detect(inputs),
                vendor => HarnessIntegrationProbe.IsWired(vendor, inputs),
                new HarnessOfferStore(config).Load(),
                loginShellFindsCli);
        } catch (Exception) {
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
