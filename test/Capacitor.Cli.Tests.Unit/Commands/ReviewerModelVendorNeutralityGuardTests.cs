using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// An architectural guard locking in vendor-neutrality for the
/// reviewer-model-override feature. The MCP layer (<c>McpFlowsServer</c>/<c>McpReviewServer</c>),
/// the shared Cli.Core wire DTOs, and the RPC coordinator/wiring (daemon ↔ server) must hold NO
/// hardcoded vendor-specific model prefix or provider→model map — a resolver owns its own vendor's
/// aliases/ids entirely (see <c>ReviewerModelResolution.cs</c>'s doc comment: "there is deliberately
/// NO shared, central vendor→model table anywhere").
///
/// <para><b>What's excluded and why:</b> ONLY the two daemon runtime resolver IMPLEMENTATIONS
/// (<c>ClaudeLauncher.cs</c> embeds <c>ClaudeReviewerModelResolver</c>, <c>CodexLauncher.cs</c> embeds
/// <c>CodexReviewerModelResolver</c>) — these legitimately own their vendor's aliases/ids. The resolver
/// coordinator/interface file <c>ReviewerModelResolution.cs</c> is deliberately NOT excluded: it is the
/// cross-vendor COORDINATOR (whose own doc comment states "there is deliberately NO shared, central
/// vendor→model table anywhere"), so a central vendor→model table wrongly added there MUST be caught. It
/// is verified token-free today, so scanning it keeps the guard GREEN. A short, per-file, per-line
/// grandfather list additionally exempts two PRE-EXISTING, unrelated vendor-model literals that predate
/// this feature entirely and are out of scope for it (see <see cref="GrandfatheredPreExistingLines"/>) —
/// every OTHER line in those same files is still scanned, so a NEW map added anywhere in them still
/// trips the guard.</para>
///
/// <para><b>Scope:</b> every <c>.cs</c> file under <c>src/</c> (not <c>test/</c>) — this is
/// production/daemon source, not test fixtures. Single-line comments (<c>//</c>/<c>///</c>) are
/// skipped: a doc-comment example (e.g. "resolves to <c>claude-sonnet-4-5-20250929</c>") is
/// documentation, not a hardcoded routing map; only CODE lines can be a real vendor→model map. Block
/// comments (<c>/* … */</c>) are not specially unwrapped — none of today's matches live inside one.</para>
/// </summary>
public class ReviewerModelVendorNeutralityGuardTests {

    /// <summary>The vendor-owned resolver IMPLEMENTATION files — the only files that legitimately own a
    /// vendor's reviewer-model aliases/ids, so never scanned. The cross-vendor coordinator
    /// (<c>ReviewerModelResolution.cs</c>) is deliberately NOT here: it must stay vendor-neutral, so a
    /// central table added to it must be caught.</summary>
    static readonly HashSet<string> ResolverOwnedFiles = new(StringComparer.OrdinalIgnoreCase) {
        "ClaudeLauncher.cs",
        "CodexLauncher.cs",
    };

    /// <summary>Per-file substrings that are PRE-EXISTING, unrelated vendor-model literals
    /// (predate this feature, no relation to the reviewer-model-override feature) — grandfathered so the
    /// guard is GREEN today without touching those working, unrelated features. Scoped by filename
    /// AND substring (not a blanket file exclusion): any other tell-tale-matching line added to
    /// either file still fails the scan.</summary>
    static readonly Dictionary<string, string[]> GrandfatheredPreExistingLines =
        new(StringComparer.OrdinalIgnoreCase) {
            // Cursor ACP session's family-prefix default model (DaemonConfig.CursorModel) — unrelated
            // daemon-wide launch config, not reviewer-model routing.
            ["DaemonConfig.cs"] = ["CursorModel { get; set; } = \"claude-sonnet-4-5\""],
            // Eval judge 1M-context-window alias rewrite (EvalService.JudgeModelFor) — unrelated
            // eval-catalog feature.
            ["EvalService.cs"] = ["\"claude-sonnet-4-6\" => \"claude-sonnet-4-6[1m]\","],
        };

    /// <summary>Tell-tale literal model-id tokens a vendor-neutral file must never contain.
    /// <c>gemini-</c> requires a digit immediately after the dash (a real model version, e.g.
    /// <c>gemini-2.5-pro</c>) so it doesn't fire on unrelated CLI tokens like
    /// <c>--gemini-settings-path</c>/<c>gemini-hook</c>/<c>gemini-root</c> that merely share the
    /// vendor name as a substring. <c>o1</c>/<c>o3</c>/<c>o4</c> (the Codex reasoning series) are
    /// word-bounded so they don't fire on arbitrary identifiers. The Codex resolver also accepts the
    /// <c>codex-*</c> family: <c>codex-mini</c> is the tight token for its one real model id
    /// (<c>codex-mini-latest</c>) — deliberately NOT a bare <c>codex-</c> pattern, which would fire on
    /// the many non-model literals (<c>codex-hook</c>, <c>--skip-codex-network-access</c>,
    /// <c>codex-unattended-v1</c>, the <c>codex-reviewer-model-v1</c> policy version, etc.).</summary>
    static readonly (string Label, Regex Pattern)[] TellTales = [
        ("claude-sonnet", new Regex(@"claude-sonnet", RegexOptions.Compiled)),
        ("claude-opus",   new Regex(@"claude-opus",   RegexOptions.Compiled)),
        ("claude-haiku",  new Regex(@"claude-haiku",  RegexOptions.Compiled)),
        ("gpt-5",         new Regex(@"gpt-5",          RegexOptions.Compiled)),
        ("gpt-4",         new Regex(@"gpt-4",          RegexOptions.Compiled)),
        ("o1",            new Regex(@"\bo1\b",         RegexOptions.Compiled)),
        ("o3",            new Regex(@"\bo3\b",         RegexOptions.Compiled)),
        ("o4",            new Regex(@"\bo4\b",         RegexOptions.Compiled)),
        ("codex-mini",    new Regex(@"codex-mini",     RegexOptions.Compiled)),
        ("gemini-<ver>",  new Regex(@"gemini-[0-9]",   RegexOptions.Compiled)),
    ];

    /// <summary>Walks up from this test file's own compile-time path (baked in by
    /// <see cref="CallerFilePathAttribute"/>, so it's independent of the test runner's working
    /// directory) until it finds the repo-root marker <c>Capacitor.slnx</c>.</summary>
    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        if (dir is null)
            throw new InvalidOperationException($"Could not locate repo root (Capacitor.slnx) walking up from {here}");

        return dir;
    }

    /// <summary>
    /// Scans every non-resolver-owned <c>.cs</c> file directly under <paramref name="srcRoot"/> for
    /// an un-grandfathered tell-tale hit on a non-comment line. Returns a human-readable violation
    /// string ("relativeOrFile:line: matched 'token' — text") per hit; empty when clean.
    /// <paramref name="srcRoot"/> is injectable so tests can point it at a synthetic fixture
    /// directory instead of the real repo (see the Scanner_* tests below) without ever writing to
    /// real source.
    /// </summary>
    internal static List<string> FindVendorNeutralityViolations(string srcRoot) {
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)) {
            var name = Path.GetFileName(file);
            if (ResolverOwnedFiles.Contains(name)) continue;

            var grandfathered = GrandfatheredPreExistingLines.TryGetValue(name, out var substrings)
                ? substrings
                : [];

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++) {
                var line    = lines[i];
                var trimmed = line.TrimStart();

                // Doc/line comments are examples, not code — a real hardcoded map lives in code.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                if (Array.Exists(grandfathered, s => line.Contains(s, StringComparison.Ordinal))) continue;

                foreach (var (label, pattern) in TellTales) {
                    if (pattern.IsMatch(line))
                        violations.Add($"{name}:{i + 1}: matched '{label}' — {line.Trim()}");
                }
            }
        }

        return violations;
    }

    // === The real guard: scans this repo's actual src/ tree ===

    [Test]
    public async Task NonResolverSource_HasNoHardcodedVendorModelKnowledge() {
        var srcRoot    = Path.Combine(RepoRoot(), "src");
        var violations = FindVendorNeutralityViolations(srcRoot);

        await Assert.That(violations).IsEmpty();
    }

    // === Scanner self-tests: prove the detector actually detects (RED) and actually excludes
    // (doesn't just vacuously pass because it excludes everything) — against a synthetic fixture
    // directory, never the real repo. ===


    [Test]
    public async Task Scanner_FlagsAHardcodedVendorModelMapOutsideResolvers() {
        // This is the TDD RED case the guard exists to catch: a NEW file outside the resolvers
        // hardcoding a vendor→model map (mirrors what a regression would look like in, say,
        // McpFlowsServer.cs or the RPC coordinator).
        using var tmp = new TempDir();

        File.WriteAllLines(tmp.PathTo("SomeNewMcpHandler.cs"), [
            "namespace Capacitor.Cli.Commands;",
            "static class SomeNewMcpHandler {",
            "    // a comment mentioning claude-opus should NOT count",
            "    static string PickModel(string vendor) => vendor switch {",
            "        \"claude\" => \"claude-opus-4-1\",",
            "        \"codex\"  => \"gpt-5-codex\",",
            "        _         => throw new ArgumentException(vendor)",
            "    };",
            "}",
        ]);

        var violations = FindVendorNeutralityViolations(tmp.Path);

        await Assert.That(violations).IsNotEmpty();
        await Assert.That(violations.Any(v => v.Contains("claude-opus"))).IsTrue();
        await Assert.That(violations.Any(v => v.Contains("gpt-5"))).IsTrue();
        // The comment line must NOT itself have contributed a violation entry.
        await Assert.That(violations.Any(v => v.Contains("a comment mentioning"))).IsFalse();
    }

    [Test]
    public async Task Scanner_ExcludesResolverOwnedLauncherFiles() {
        // The SAME hardcoded map, but living in a file named like a resolver-owned launcher —
        // proves the exclusion is by filename, not a blanket "scanner finds nothing" bug.
        using var tmp = new TempDir();

        File.WriteAllLines(tmp.PathTo("ClaudeLauncher.cs"), [
            "namespace Capacitor.Cli.Daemon.Services;",
            "static class ClaudeReviewerModelResolver {",
            "    public static string Resolve() => \"claude-opus-4-1\";",
            "}",
        ]);
        // A sibling non-resolver file in the SAME directory proves the scan still runs at all.
        File.WriteAllLines(tmp.PathTo("UnrelatedHelper.cs"), [
            "namespace Capacitor.Cli.Daemon.Services;",
            "static class UnrelatedHelper { }",
        ]);

        var violations = FindVendorNeutralityViolations(tmp.Path);

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task Scanner_GrandfathersOnlyTheDocumentedPreExistingLine_NotOtherLinesInTheSameFile() {
        using var tmp = new TempDir();

        File.WriteAllLines(tmp.PathTo("DaemonConfig.cs"), [
            "namespace Capacitor.Cli.Daemon;",
            "class DaemonConfig {",
            "    public string CursorModel { get; set; } = \"claude-sonnet-4-5\";", // grandfathered
            "    public string SomethingElse { get; set; } = \"claude-opus-4-1\";", // NOT grandfathered
            "}",
        ]);

        var violations = FindVendorNeutralityViolations(tmp.Path);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("claude-opus");
    }
}
