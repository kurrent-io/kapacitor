using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// hidden diagnostic: the phase-0 empirical verification harness. Samples
/// <c>(length, sha256(prefix))</c> off a live Cursor transcript file at ~1s cadence for a bounded
/// duration and checks every earlier/later sample pair with
/// <see cref="CursorAppendOnlyProbe.PrefixStable"/>, printing a PASS/FAIL report.
///
/// This is the evidence gate for the rest of the CLI-side work (Tasks 8-12 per the plan's Task 7
/// header): a Cursor session's transcript is run through this while it's actively being written
/// to; a FAIL means Cursor rewrites its own transcript in place and the whole watcher-promotion
/// design needs to be reconsidered before proceeding. The runtime <see cref="CursorRewriteGuard"/>
/// ships regardless, as defence-in-depth.
///
/// Not listed in <c>help-usage.txt</c> — internal, run manually by whoever is gathering that
/// evidence: <c>kcap cursor-verify-appendonly --path &lt;file&gt; [--duration-seconds 60] [--interval-seconds 1]</c>.
/// AOT-safe: no reflection, no JSON — plain text sampling and reporting only.
/// </summary>
public static class CursorVerifyAppendOnlyCommand {
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default) {
        var path = GetArg(args, "--path");

        if (string.IsNullOrEmpty(path)) {
            await Console.Error.WriteLineAsync(
                "Usage: kcap cursor-verify-appendonly --path <file> [--duration-seconds 60] [--interval-seconds 1]");

            return 1;
        }

        if (!File.Exists(path)) {
            await Console.Error.WriteLineAsync($"File not found: {path}");

            return 1;
        }

        var durationSeconds = ParseIntArg(args, "--duration-seconds", 60);
        var intervalSeconds = ParseIntArg(args, "--interval-seconds", 1);
        var deadline         = DateTimeOffset.UtcNow.AddSeconds(durationSeconds);

        var samples  = new List<CursorAppendOnlyProbe.Sample>();
        var failures = new List<string>();

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested) {
            byte[] bytes;

            try {
                bytes = await File.ReadAllBytesAsync(path, ct);
            } catch (IOException) {
                // Racing a concurrent writer — skip this tick rather than fail the whole run.
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);

                continue;
            }

            var sample = new CursorAppendOnlyProbe.Sample(bytes.LongLength, CursorAppendOnlyProbe.Sha256Hex(bytes));

            for (var i = 0; i < samples.Count; i++) {
                if (!CursorAppendOnlyProbe.PrefixStable(samples[i], sample, bytes)) {
                    failures.Add(
                        $"sample #{i} (length {samples[i].Length}) is not a stable prefix of sample #{samples.Count} (length {sample.Length})");
                }
            }

            samples.Add(sample);

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }

        await Console.Out.WriteLineAsync(
            $"cursor-verify-appendonly: {samples.Count} samples of '{path}' over {durationSeconds}s (interval {intervalSeconds}s)");

        if (failures.Count == 0) {
            await Console.Out.WriteLineAsync("PASS — append-only held across every sampled pair.");

            return 0;
        }

        await Console.Out.WriteLineAsync($"FAIL — {failures.Count} prefix-stability violation(s):");

        foreach (var failure in failures) {
            await Console.Out.WriteLineAsync($"  {failure}");
        }

        return 1;
    }

    static int ParseIntArg(string[] args, string flag, int defaultValue) {
        var raw = GetArg(args, flag);

        return raw is not null && int.TryParse(raw, out var value) ? value : defaultValue;
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
