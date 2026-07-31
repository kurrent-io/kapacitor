using System.Runtime.CompilerServices;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Pins the "never lock out the writing agent" invariant on the transcript-reading paths.
/// <para>Why a SOURCE check rather than a behavioural one: the invariant is a <c>FileShare</c> mode,
/// and its violation is only observable where file sharing is mandatory — Windows. On macOS/Linux a
/// <c>FileShare.Read</c> open does not actually deny a concurrent writer, so a behavioural test passes
/// identically with and without the fix (verified: reverting the fix left the behavioural test green
/// locally). A source assertion discriminates on every platform, so the guard cannot rot on the two
/// platforms most contributors run.</para>
/// <para><c>File.ReadAllText</c>/<c>ReadAllTextAsync</c> open <c>FileShare.Read</c>, which denies Write
/// for the duration of the read. The agent owns these files and is often still appending to them —
/// most critically during the shutdown final drain, when it is flushing its last records.</para>
/// </summary>
public class TranscriptReadSharingInvariantTests {
    /// <summary>Repo-root-relative path to the file under guard, resolved from this test's own compiled
    /// source location so it works from any working directory.</summary>
    static string WatchCommandSource() {
        var here = ThisFile();                                   // …/test/Capacitor.Cli.Tests.Unit/<this>.cs
        var repo = Directory.GetParent(here)!.Parent!.Parent!;    // …/ (repo root)

        return Path.Combine(repo.FullName, "src", "Capacitor.Cli", "Commands", "WatchCommand.cs");
    }

    static string ThisFile([CallerFilePath] string path = "") => path;

    [Test]
    public async Task WatchCommand_never_reads_a_transcript_with_a_write_denying_open() {
        var source = WatchCommandSource();

        // Guard the guard: if the layout moves, fail loudly rather than pass vacuously on a missing file.
        await Assert.That(File.Exists(source)).IsTrue();

        var offending = File.ReadAllLines(source)
            .Select((text, index) => (Line: index + 1, Text: text))
            // Only real calls — the doc comments deliberately NAME the banned API to explain the rule.
            .Where(l => !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Where(l => l.Text.Contains("File.ReadAllText(", StringComparison.Ordinal)
                     || l.Text.Contains("File.ReadAllTextAsync(", StringComparison.Ordinal))
            .Select(l => $"{l.Line}: {l.Text.Trim()}")
            .ToArray();

        // WatchCommand.ReadAllTextShared/Async are the sanctioned readers (FileShare.ReadWrite).
        await Assert.That(offending).IsEmpty();
    }
}
