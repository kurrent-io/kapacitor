using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Pure parser that resolves the set of selected import sources from args.
/// Empty vendor set = "all detected" (orchestrator handles availability).
/// Returns a structured Error for vendor-flag typos; leaves all other --flags
/// untouched so unrelated globals (--server-url, --no-update-check, --profile, ...)
/// flow through dispatch unchanged.
/// </summary>
public static class VendorSelection {
    public sealed record Result(IReadOnlySet<HarnessId> Vendors, string? Error) {
        public bool HasError => Error is not null;
    }

    // internal, not private: the driver-schema conformance suite pins its hand-written harness table
    // against this list, so adding a tenth installable target fails there instead of silently leaving
    // it uncovered. No enumeration of supported harnesses exists in production code otherwise.
    internal static readonly string[] KnownVendorFlags = ["--claude", "--codex", "--cursor", "--copilot", "--gemini", "--kiro", "--pi", "--opencode", "--antigravity"];

    public static Result Parse(string[] args) {
        var vendors = new HashSet<HarnessId>();

        foreach (var a in args) {
            switch (a) {
                case "--claude":  vendors.Add(HarnessId.Claude);  break;
                case "--codex":   vendors.Add(HarnessId.Codex);   break;
                case "--cursor":  vendors.Add(HarnessId.Cursor);  break;
                case "--copilot": vendors.Add(HarnessId.Copilot); break;
                case "--gemini":  vendors.Add(HarnessId.Gemini);  break;
                case "--kiro":    vendors.Add(HarnessId.Kiro);    break;
                case "--pi":      vendors.Add(HarnessId.Pi);      break;
                case "--opencode": vendors.Add(HarnessId.OpenCode); break;
                case "--antigravity": vendors.Add(HarnessId.Antigravity); break;
            }
        }

        // Reject unknown vendor-prefixed flags. The legacy SQLite-era options
        // (--cursor-workspace, --cursor-all-workspaces) were dropped;
        // the JSONL walkers don't need workspace filtering since the
        // transcript path already encodes the workspace.
        foreach (var a in args) {
            if (!a.StartsWith("--")) continue;
            if (Array.IndexOf(KnownVendorFlags, a) >= 0) continue;

            if (a.StartsWith("--cursor-") || a.StartsWith("--claude-") || a.StartsWith("--codex-") || a.StartsWith("--copilot-") || a.StartsWith("--gemini-") || a.StartsWith("--kiro-") || a.StartsWith("--pi-") || a.StartsWith("--opencode-") || a.StartsWith("--antigravity-")) {
                return new(vendors, $"Unknown source option: {a}.");
            }
        }

        // Vendor-typo detection (Damerau-Levenshtein <= 2 against vendor flags).
        foreach (var a in args) {
            if (!a.StartsWith("--")) continue;
            if (Array.IndexOf(KnownVendorFlags, a) >= 0) continue;
            if (a.StartsWith("--cursor-") || a.StartsWith("--claude-") || a.StartsWith("--codex-") || a.StartsWith("--copilot-") || a.StartsWith("--gemini-") || a.StartsWith("--kiro-") || a.StartsWith("--pi-") || a.StartsWith("--opencode-") || a.StartsWith("--antigravity-")) continue;

            string? hint = null;
            var bestDist = int.MaxValue;
            foreach (var v in KnownVendorFlags) {
                var d = DamerauLevenshtein(a, v);
                if (d < bestDist) { bestDist = d; hint = v; }
            }
            if (bestDist is > 0 and <= 2) {
                return new(vendors, $"Unknown vendor flag: {a}. Did you mean {hint}?");
            }
        }

        return new(vendors, null);
    }

    static int DamerauLevenshtein(string a, string b) {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++) {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + cost);
            }
        return d[a.Length, b.Length];
    }
}
