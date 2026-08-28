using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap harness</c> — inspect and control the new-harness setup nudges. <c>list</c> shows each
/// vendor's detected / kcap-wired / dismissed state; <c>dismiss</c> permanently silences a vendor's
/// nudge; <c>reset</c> undoes a dismissal. All three run their own detection pass and neither read
/// nor claim the shared 6-hour evaluation throttle (the nudge surfaces' concern, not the commands').
/// </summary>
public sealed class HarnessCommand(ConfigRoot config, UserHome home) {
    public Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) { PrintUsage(); return Task.FromResult(1); }

        var store    = new HarnessOfferStore(config);
        var paths    = HarnessPaths.FromEnvironment(home);
        var detected = AgentDetection.Detect(paths, BinaryProbe.FromEnvironment());

        return Task.FromResult(args[1] switch {
            "list"                    => List(detected, paths, store),
            "dismiss"                 => Dismiss(args, detected, paths, store),
            "reset"                   => Reset(args, store),
            "--help" or "-h" or "help" => Help(),
            _                         => Unknown(args[1]),
        });
    }

    static int List(AgentDetectionResult detected, HarnessPaths paths, HarnessOfferStore store) {
        var ledger = store.Load();
        Console.WriteLine($"  {"Harness",-14}{"Installed",-11}{"kcap wired",-12}Dismissed");
        foreach (var h in HarnessCatalog.All) {
            var isDetected = h.Select(detected).Detected;
            var isWired    = paths.IsWired(h.VendorId);
            var isDismissed = ledger.Entry(h.VendorId) is { Declined: true };
            Console.WriteLine($"  {h.Label,-14}{YesNo(isDetected),-11}{YesNo(isWired),-12}{YesNo(isDismissed)}");
        }
        return 0;
    }

    static int Dismiss(string[] args, AgentDetectionResult detected, HarnessPaths paths, HarnessOfferStore store) {
        var rest = args.Skip(2).ToArray();
        List<KnownHarness> targets;

        if (rest.Contains("--all")) {
            // Exactly the currently detected-and-unwired set — deliberately NOT all nine: a harness
            // installed after this dismiss is a new event and nudges once. The "never ask about any
            // harness" switch is the `disable_harness_nudge` profile setting, not `--all`.
            targets = HarnessCatalog.All
                .Where(h => h.Select(detected).Detected && !paths.IsWired(h.VendorId))
                .ToList();
            if (targets.Count == 0) {
                Console.WriteLine("No detected-but-unconfigured harnesses to dismiss.");
                return 0;
            }
        } else {
            var ids = rest.Where(a => !a.StartsWith('-')).ToArray();
            if (ids.Length == 0) {
                Console.Error.WriteLine("kcap harness dismiss requires a vendor id (e.g. antigravity) or --all.");
                return 1;
            }
            targets = new List<KnownHarness>();
            foreach (var id in ids) {
                if (HarnessCatalog.ById(id) is { } h) targets.Add(h);
                else { Console.Error.WriteLine(UnknownVendor(id)); return 1; }
            }
        }

        var now = DateTimeOffset.UtcNow;
        if (!store.Update(l => l with { Vendors = WithDismissed(l, targets, now) })) {
            Console.Error.WriteLine("kcap: could not persist the dismissal (failed to write the offer ledger).");
            return 1;
        }
        Console.WriteLine($"Dismissed: {string.Join(", ", targets.Select(t => t.Label))}. Re-enable with `kcap harness reset <vendor>`.");
        return 0;
    }

    static int Reset(string[] args, HarnessOfferStore store) {
        var rest = args.Skip(2).ToArray();
        var ledger = store.Load();

        List<string> ids;
        if (rest.Contains("--all")) {
            ids = ledger.Vendors.Keys.ToList();
        } else {
            ids = rest.Where(a => !a.StartsWith('-')).ToList();
            if (ids.Count == 0) {
                Console.Error.WriteLine("kcap harness reset requires a vendor id (e.g. antigravity) or --all.");
                return 1;
            }
            foreach (var id in ids)
                if (HarnessCatalog.ById(id) is null) { Console.Error.WriteLine(UnknownVendor(id)); return 1; }
        }

        // Removing the entry clears both the dismissal and the last-offered stamp, so the vendor is
        // treated as freshly seen and nudges again on the next eligible evaluation.
        if (!store.Update(l => l with { Vendors = Without(l, ids) })) {
            Console.Error.WriteLine("kcap: could not persist the reset (failed to write the offer ledger).");
            return 1;
        }
        Console.WriteLine($"Reset: {string.Join(", ", ids)}. These will be offered again.");
        return 0;
    }

    static Dictionary<string, HarnessOfferEntry> WithDismissed(HarnessOfferLedger l, IEnumerable<KnownHarness> targets, DateTimeOffset now) {
        var vendors = new Dictionary<string, HarnessOfferEntry>(l.Vendors);
        foreach (var h in targets) {
            var prior = l.Entry(h.VendorId);
            vendors[h.VendorId] = new HarnessOfferEntry {
                FirstSeen   = prior?.FirstSeen ?? now,
                LastOffered = prior?.LastOffered,
                Declined    = true,
            };
        }
        return vendors;
    }

    static Dictionary<string, HarnessOfferEntry> Without(HarnessOfferLedger l, IEnumerable<string> ids) {
        var vendors = new Dictionary<string, HarnessOfferEntry>(l.Vendors);
        foreach (var id in ids) vendors.Remove(id);
        return vendors;
    }

    static int Help() {
        PrintUsage();
        return 0;
    }

    static int Unknown(string sub) {
        Console.Error.WriteLine($"Unknown harness subcommand: {sub}");
        PrintUsage();
        return 1;
    }

    static string YesNo(bool b) => b ? "yes" : "no";

    static string UnknownVendor(string id) =>
        $"Unknown harness: {id}. Known: {string.Join(", ", HarnessCatalog.All.Select(h => h.VendorId))}.";

    static void PrintUsage() {
        Console.Error.WriteLine("Usage: kcap harness <list|dismiss|reset>");
        Console.Error.WriteLine("  list                     show detected / kcap-wired / dismissed state per harness");
        Console.Error.WriteLine("  dismiss <vendor…>|--all  stop nudging to set kcap up for a harness");
        Console.Error.WriteLine("  reset <vendor…>|--all    offer a previously-dismissed harness again");
    }
}
