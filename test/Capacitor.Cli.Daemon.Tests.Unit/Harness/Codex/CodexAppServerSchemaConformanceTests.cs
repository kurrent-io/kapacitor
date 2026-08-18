using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// Schema conformance for the <c>codex app-server</c> protocol. Pins the subset
/// of the protocol schema the runtime depends on (<see cref="CodexAppServerSchemaSubset"/>) and, on a
/// machine with <c>codex</c> installed, regenerates that schema from the binary and diffs it against
/// the vendored pin — so a version that changes a depended-on shape fails HERE instead of silently at
/// launch.
///
/// <para>Two arms: a CI-safe arm that validates the committed pin documents every depended-on shape
/// (no binary needed — CI has no <c>codex</c>), and a binary-gated arm that catches real upstream
/// drift (auto-skips when the binary is absent — never a silent pass). Part (b) — the behavioural Q1
/// isolation / Q2 sandbox probes — lives with the live smoke, not here.</para>
///
/// <para>Regenerate the pin after an intended Codex bump (having re-vetted the behavioural probes):
/// <c>KCAP_CODEX_SCHEMA_PIN_UPDATE=1</c> with <c>codex</c> on PATH rewrites it from the installed
/// binary through the exact extractor the diff uses.</para>
/// </summary>
public class CodexAppServerSchemaConformanceTests {
    const string PinUpdateEnvVar = "KCAP_CODEX_SCHEMA_PIN_UPDATE";
    static readonly TimeSpan ProcessGuard = TimeSpan.FromSeconds(30);

    // ── CI-safe arm: the committed pin documents every depended-on shape ───────────────────────

    [Test]
    public async Task Vendored_pin_documents_every_depended_on_shape() {
        var pin        = LoadPin();
        var combined   = pin["combinedDefs"]!.AsObject();
        var standalone = pin["standalone"]!.AsObject();

        var missingRoots = CodexAppServerSchemaSubset.RootDefs.Where(r => !combined.ContainsKey(r)).ToList();
        await Assert.That(missingRoots).IsEmpty();

        var missingFiles = CodexAppServerSchemaSubset.StandaloneFiles.Where(f => !standalone.ContainsKey(f)).ToList();
        await Assert.That(missingFiles).IsEmpty();

        // The exact fields the runtime writes on turn/start and thread/start, and reads off the
        // usage notification — a corruption guard and a living manifest of what we depend on.
        await AssertProperties(combined, "TurnStartParams",
            "threadId", "input", "sandboxPolicy", "approvalPolicy", "approvalsReviewer", "model", "effort");
        await AssertProperties(combined, "ThreadStartParams",
            "cwd", "sandbox", "approvalPolicy", "approvalsReviewer", "model");
        await AssertProperties(combined, "ThreadTokenUsageUpdatedNotification",
            "threadId", "turnId", "tokenUsage");
        await AssertProperties(combined, "TokenUsageBreakdown",
            "inputTokens", "cachedInputTokens", "outputTokens", "reasoningOutputTokens", "totalTokens");

        // The posture depends on these enum tokens being accepted verbatim (SandboxPolicy variant
        // discriminators and the kebab-case approval strings).
        await AssertEnumMembers(combined["SandboxPolicy"], "readOnly", "workspaceWrite", "dangerFullAccess");
        await AssertEnumMembers(combined["AskForApproval"], "never", "on-request", "untrusted");
    }

    // ── CI-safe arm: the diff actually catches a depended-on shape change ───────────────────────

    [Test]
    public async Task Diff_catches_a_mutated_depended_on_shape() {
        var pin     = LoadPin();
        var mutated = JsonNode.Parse(pin.ToJsonString())!.AsObject();

        // Remove a load-bearing property from a copy — the drift a real breaking bump would produce.
        mutated["combinedDefs"]!["TurnStartParams"]!["properties"]!.AsObject().Remove("sandboxPolicy");

        var diff = Diff(pin, mutated);
        await Assert.That(diff).IsNotNull();
        await Assert.That(diff!).Contains("TurnStartParams");
    }

    // ── Binary-gated arm: the installed codex schema still matches the pin ──────────────────────

    [Test]
    public async Task Installed_codex_schema_matches_the_vendored_pin() {
        var (found, version) = await TryResolveCodexVersionAsync();
        Skip.When(!found,
            "codex not resolvable on PATH — the schema-drift check needs a local codex install (shared CI has none).");

        using var outDir = new TempDir();
        await GenerateSchemaAsync(outDir.Path);
        var fresh = CodexAppServerSchemaSubset.Extract(outDir.Path, version);

        if (Environment.GetEnvironmentVariable(PinUpdateEnvVar) == "1") {
            var path = PinPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, CodexAppServerSchemaSubset.Serialize(fresh));
            Skip.Test($"Vendored pin regenerated from codex {version}. Re-run without {PinUpdateEnvVar}=1 to verify.");
        }

        var pin  = LoadPin();
        var diff = Diff(pin, fresh);

        var report = diff is null ? null
            : $"codex app-server schema (installed codex {version}) diverged from the vendored pin "
            + $"(pinned from codex {pin["codexVersion"]?.GetValue<string>()}):\n{diff}\n"
            + $"If this is an intended Codex bump, re-vet the Q1/Q2 behavioural probes, then regenerate "
            + $"the pin with {PinUpdateEnvVar}=1.";

        await Assert.That(report).IsNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    static async Task AssertProperties(JsonObject combined, string def, params string[] required) {
        var props = combined[def]?.AsObject()?["properties"]?.AsObject();
        await Assert.That(props).IsNotNull();

        var missing = required.Where(r => !props!.ContainsKey(r)).ToList();
        await Assert.That(missing).IsEmpty();
    }

    // Asserts the tokens are real enum MEMBERS of the pinned def (values inside some "enum": [...]),
    // not just substrings of its serialized JSON — so a token that appears in a description string
    // can't satisfy the check.
    static async Task AssertEnumMembers(JsonNode? def, params string[] tokens) {
        var members = EnumValues(def);
        var missing = tokens.Where(t => !members.Contains(t)).ToList();
        await Assert.That(missing).IsEmpty();
    }

    static HashSet<string> EnumValues(JsonNode? node) {
        var acc = new HashSet<string>(StringComparer.Ordinal);
        Collect(node, acc);
        return acc;

        static void Collect(JsonNode? n, HashSet<string> acc) {
            switch (n) {
                case JsonObject o:
                    foreach (var (key, value) in o) {
                        if (key == "enum" && value is JsonArray items) {
                            foreach (var item in items)
                                if (item is JsonValue v && v.TryGetValue<string>(out var s)) acc.Add(s);
                        } else {
                            Collect(value, acc);
                        }
                    }
                    break;
                case JsonArray a:
                    foreach (var x in a) Collect(x, acc);
                    break;
            }
        }
    }

    /// <summary>Per-key by-value comparison of the two pinnable sections; a readable report of the
    /// keys that were removed / added / changed, or null when identical.</summary>
    static string? Diff(JsonObject pin, JsonObject fresh) {
        var report = new StringBuilder();
        DiffSection(report, "combinedDefs", pin["combinedDefs"]!.AsObject(), fresh["combinedDefs"]!.AsObject());
        DiffSection(report, "standalone",   pin["standalone"]!.AsObject(),   fresh["standalone"]!.AsObject());
        return report.Length == 0 ? null : report.ToString();
    }

    static void DiffSection(StringBuilder report, string label, JsonObject pin, JsonObject fresh) {
        var keys = pin.Select(kv => kv.Key).Union(fresh.Select(kv => kv.Key)).OrderBy(k => k, StringComparer.Ordinal);
        foreach (var key in keys) {
            var inPin   = pin.ContainsKey(key);
            var inFresh = fresh.ContainsKey(key);
            if (!inFresh)
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"  [{label}] '{key}': in pin, MISSING from installed codex (removed/renamed upstream).");
            else if (!inPin)
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"  [{label}] '{key}': present in installed codex, NOT in pin (add + re-vet).");
            else if (CodexAppServerSchemaSubset.Canonical(pin[key]) != CodexAppServerSchemaSubset.Canonical(fresh[key]))
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"  [{label}] '{key}': shape changed vs pin.");
        }
    }

    static JsonObject LoadPin() {
        var path = PinPath();
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Vendored codex app-server schema pin missing at '{path}'. Generate it on a machine with codex "
              + $"installed: {PinUpdateEnvVar}=1 dotnet run --project "
              + "test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj.", path);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    static string PinPath([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "AppServerSchema", "codex-app-server-subset.pin.json");

    static async Task<(bool Found, string Version)> TryResolveCodexVersionAsync() {
        Process process;
        try {
            var started = Process.Start(new ProcessStartInfo("codex", ["--version"]) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (started is null) return (false, "");
            process = started;
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException) {
            return (false, ""); // codex not on PATH
        }

        using (process) {
            var (stdout, _, _) = await ReadToExitAsync(process);
            var match = Regex.Match(stdout, @"\d+\.\d+\.\d+");
            // codex present but no parseable version → treat as not-found and skip, rather than run the
            // drift arm with an empty, misleading version stamp.
            return match.Success ? (true, match.Value) : (false, "");
        }
    }

    static async Task GenerateSchemaAsync(string outDir) {
        using var process = Process.Start(new ProcessStartInfo("codex", ["app-server", "generate-json-schema", "--out", outDir]) {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        }) ?? throw new InvalidOperationException("codex app-server generate-json-schema did not start.");

        var (_, stderr, timedOut) = await ReadToExitAsync(process);
        if (timedOut)
            throw new InvalidOperationException("codex app-server generate-json-schema timed out.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"codex app-server generate-json-schema exited {process.ExitCode}: {stderr}");
    }

    // Drains stdout AND stderr concurrently while waiting for exit, so a full pipe on either stream
    // can never deadlock a WaitForExit (the classic single-stream-then-wait trap). On timeout the
    // process tree is killed and TimedOut is returned.
    static async Task<(string Stdout, string Stderr, bool TimedOut)> ReadToExitAsync(Process process) {
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(ProcessGuard);
        try {
            await process.WaitForExitAsync(cts.Token);
        } catch (OperationCanceledException) {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return ("", "", true);
        }
        return (await stdoutTask, await stderrTask, false);
    }
}
