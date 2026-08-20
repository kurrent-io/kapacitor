namespace Capacitor.Cli.Core.Setup;

/// <summary>One vendor's line in a <see cref="HarnessInventory"/>: is the harness installed on this
/// machine, and is kcap wired into it.</summary>
public sealed record HarnessInventoryEntry(bool Detected, bool Wired);

/// <summary>
/// A machine's coding-agent inventory reported to the server (surface 3): for every supported
/// vendor, whether it's detected and whether kcap is wired in, plus the vendors the user has
/// dismissed. The server raises the "installed but not configured" notification for a vendor that
/// is <c>detected &amp;&amp; !wired</c> and not in <see cref="Declined"/>. Additive on the wire — an
/// older server ignores it; an older client never sends it (the server treats an absent inventory
/// as "unknown", never "nothing detected").
///
/// <para>Serialized snake_case by <c>CapacitorJsonContext</c> (<c>machine_id</c>, <c>vendors</c>,
/// <c>detected</c>, <c>wired</c>, <c>declined</c>). <c>MachineId</c> is <see cref="Core.MachineId.Get"/>
/// — the same id the daemon sends on <c>DaemonConnect</c> — so the server correlates a machine's
/// connect record, status-report inventory, and hook-ingest inventory to one machine.</para>
/// </summary>
public sealed record HarnessInventory(
    string MachineId,
    Dictionary<string, HarnessInventoryEntry> Vendors,
    string[] Declined) {

    /// <summary>Pure: computes the inventory from an injected detection snapshot + offer ledger +
    /// machine id, over every <see cref="HarnessCatalog"/> vendor. No I/O, no throttle — both
    /// carriers (daemon status report, hook ingest) share this.</summary>
    public static HarnessInventory Evaluate(AgentDetectionInputs inputs, HarnessOfferLedger ledger, string machineId) =>
        Evaluate(AgentDetection.Detect(inputs), id => HarnessIntegrationProbe.IsWired(id, inputs), ledger, machineId);

    /// <summary>Injectable core (detection result + wired-probe + ledger already resolved), so the
    /// vendor→entry mapping is unit-testable without touching the filesystem or PATH.</summary>
    public static HarnessInventory Evaluate(
            AgentDetectionResult detected, Func<string, bool> isWired, HarnessOfferLedger ledger, string machineId) {
        var vendors = new Dictionary<string, HarnessInventoryEntry>(StringComparer.Ordinal);
        var declined = new List<string>();

        foreach (var h in HarnessCatalog.All) {
            vendors[h.VendorId] = new HarnessInventoryEntry(
                Detected: h.Select(detected).Detected,
                Wired: isWired(h.VendorId));
            if (ledger.Entry(h.VendorId) is { Declined: true }) declined.Add(h.VendorId);
        }

        return new HarnessInventory(machineId, vendors, [.. declined]);
    }

    /// <summary>Production convenience: evaluate from the current process environment, the default
    /// on-disk offer ledger (read-only — never claims the throttle stamp), and this machine's id.</summary>
    public static HarnessInventory EvaluateCurrent() =>
        Evaluate(AgentDetection.FromEnvironment(), HarnessOfferStore.Default().Load(), Core.MachineId.Get());
}
