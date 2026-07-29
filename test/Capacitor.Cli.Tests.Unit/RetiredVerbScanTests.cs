namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The daemon verb is `kcap daemon …`; the old verb was retired in May 2026 but kept resurfacing
/// in shipped text — the flow skills taught it for two error codes long after the server corrected
/// its own messages, overriding the fix downstream. This scan keeps the dead verb out of everything
/// this repo ships to users and agents: the kcap/ plugin (skills included) and src/. Historical
/// docs/ artifacts are deliberately out of scope, as is test/ (this file must name the phrase in
/// order to ban it).
/// </summary>
public class RetiredVerbScanTests {
    /// <summary>Kept as a literal here, in a test, precisely because it must not appear anywhere
    /// under the scanned roots.</summary>
    const string RetiredVerb = "kcap agent";

    static readonly string[] ScannedRoots = ["kcap", "src"];

    /// <summary>Formats that cannot carry a readable instruction. Everything else under the
    /// scanned roots is text until proven otherwise — an allowlist of "text" extensions misses
    /// exactly the files that ship words (markdown, yaml, prompt txt).</summary>
    static readonly string[] BinaryExtensions = [
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".svgz",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".pdf", ".zip", ".gz", ".dll", ".dylib", ".so", ".exe"
    ];

    static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules"];

    static DirectoryInfo RepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Capacitor.slnx")))
            dir = dir.Parent;

        return dir ?? throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    static IEnumerable<string> ShippedFiles(string repoRoot) {
        foreach (var rootName in ScannedRoots) {
            var root = Path.Combine(repoRoot, rootName);
            if (!Directory.Exists(root)) continue;

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                var segments = Path.GetRelativePath(root, path)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (segments[..^1].Any(s => SkippedDirectories.Contains(s, StringComparer.OrdinalIgnoreCase))) continue;
                if (BinaryExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;

                yield return path;
            }
        }
    }

    [Test]
    public async Task No_shipped_text_names_the_retired_daemon_verb() {
        var root = RepoRoot().FullName;

        List<string> findings = [];

        foreach (var path in ShippedFiles(root)) {
            var lines = await File.ReadAllLinesAsync(path);

            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains(RetiredVerb, StringComparison.OrdinalIgnoreCase))
                    findings.Add($"  {Path.GetRelativePath(root, path)}:{i + 1}: {lines[i].Trim()}");
        }

        await Assert.That(findings)
            .IsEmpty()
            .Because(
                $"the daemon verb is `kcap daemon …`. Rephrase these, or — for a genuinely "
              + $"historical mention — avoid the exact phrase:\n{string.Join("\n", findings)}");
    }
}
