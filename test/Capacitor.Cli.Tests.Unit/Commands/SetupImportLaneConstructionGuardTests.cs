using System.Runtime.CompilerServices;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <see cref="SetupImportLane"/> imports through <c>ImportCommand</c>, which takes its base URL from
/// the <c>ProfileContext</c> it is handed and from nowhere else. The browser leg's own context is the
/// snapshot resolved before Step 1, so on a first run it names no server — and <c>setup</c> is exempt
/// from the entry gate that refuses an unconfigured command, so an unusable URL is caught by nothing
/// until it reaches the client factory.
///
/// <para>A guard rather than a unit test because the defect is in WHICH context the call site passes,
/// and the call site sits inside an authenticated leg that a unit test cannot reach. Pinning
/// <see cref="SetupCommand.ImportContext"/> alone would pass with nothing calling it.</para>
/// </summary>
public class SetupImportLaneConstructionGuardTests {
    /// <summary>Walks up from this file to the repo-root marker, so the test runner's working
    /// directory is irrelevant.</summary>
    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"Could not locate repo root walking up from {here}");
    }

    [Test]
    public async Task Every_construction_of_the_lane_names_the_server_this_run_resolved() {
        var sites = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), No: i + 1, line)))
            .Where(l => l.line.Contains("new SetupImportLane(", StringComparison.Ordinal))
            .ToList();

        await Assert.That(sites).IsNotEmpty()
                    .Because("a rename that empties this scan would make the guard pass for the wrong reason");

        foreach (var site in sites) {
            await Assert.That(site.line).Contains("ImportContext(")
                        .Because($"{site.File}:{site.No} hands the lane a context that need not name a server");
        }
    }
}
