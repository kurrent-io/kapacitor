using System.Reflection;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Guards the "never lock out the writing agent" rule on <c>WatchCommand</c>'s transcript reads:
/// <c>File.ReadAllText</c>/<c>ReadAllTextAsync</c> open <c>FileShare.Read</c>, which denies Write for
/// the duration of the read. The agent owns those files and is often still appending — most critically
/// during the shutdown final drain, when it is flushing its last records.
/// <para><b>Why a source check.</b> The invariant is a <c>FileShare</c> mode, and violating it is only
/// observable where sharing is mandatory — Windows. On macOS/Linux a <c>FileShare.Read</c> open does
/// not actually deny a concurrent writer, so the behavioural test in
/// <c>WaitForFinalLineCompletionAsyncTests</c> passes identically with and without the fix (verified by
/// reverting it). This check discriminates on every platform, so the rule cannot rot on the two
/// platforms most contributors run.</para>
/// <para><b>Scope, honestly.</b> This is a textual check, not a semantic one. It tolerates whitespace
/// and line breaks inside the call and ignores comments, but it can still be bypassed by a type alias
/// (<c>using IOFile = System.IO.File;</c>) or a static import (<c>using static System.IO.File;</c>).
/// It is a backstop against the accidental reintroduction this PR fixes, not a proof of absence — the
/// authoritative behavioural coverage is the Windows CI leg.</para>
/// </summary>
public class TranscriptReadSharingInvariantTests {
    const string GuardedResource = "guarded.WatchCommand.cs";

    /// <summary>Whitespace-tolerant and line-break-tolerant: matches `File . ReadAllText (` and
    /// `File.ReadAllTextAsync\n(` as well as the tight form. Also catches a fully-qualified
    /// `System.IO.File.ReadAllText(` since that still ends in the same token sequence.</summary>
    static readonly Regex WriteDenyingRead =
        new(@"\bFile\s*\.\s*ReadAllText(Async)?\s*\(", RegexOptions.Compiled);

    /// <summary>Strips block and line comments so the doc comments — which deliberately NAME the banned
    /// API to explain the rule — cannot trip the scan, and so a commented-out call cannot either.</summary>
    static string StripComments(string source) {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
    }

    [Test]
    public async Task WatchCommand_has_no_write_denying_transcript_read() {
        await using var stream = typeof(TranscriptReadSharingInvariantTests).Assembly
            .GetManifestResourceStream(GuardedResource);

        // Guard the guard: a renamed/dropped resource must fail loudly, not pass vacuously.
        await Assert.That(stream).IsNotNull();

        using var reader = new StreamReader(stream!);
        var       source = StripComments(await reader.ReadToEndAsync());

        var offending = WriteDenyingRead.Matches(source)
            .Select(m => source[..m.Index].Count(c => c == '\n') + 1) // 1-based line of each hit
            .ToArray();

        // WatchCommand.ReadAllTextShared/Async are the sanctioned readers (FileShare.ReadWrite).
        await Assert.That(offending).IsEmpty();
    }

    /// <summary>Proves the scan can actually see a violation — without this, a regex that matched
    /// nothing at all would make the test above pass forever.</summary>
    [Test]
    public async Task the_scan_detects_a_write_denying_read_in_every_spelling() {
        foreach (var spelling in new[] {
                     "var t = File.ReadAllText(path);",
                     "var t = await File.ReadAllTextAsync(path);",
                     "var t = File.ReadAllTextAsync (path);",
                     "var t = File\n    .ReadAllText(path);",
                     "var t = System.IO.File.ReadAllText(path);",
                 }) {
            await Assert.That(WriteDenyingRead.IsMatch(StripComments(spelling))).IsTrue();
        }

        // …and does not fire on the sanctioned helpers or on prose mentioning the API.
        foreach (var benign in new[] {
                     "var t = await ReadAllTextSharedAsync(path);",
                     "var t = ReadAllTextShared(path);",
                     "/// File.ReadAllText opens FileShare.Read, which is why this helper exists.",
                     "// var t = File.ReadAllText(path);",
                 }) {
            await Assert.That(WriteDenyingRead.IsMatch(StripComments(benign))).IsFalse();
        }
    }
}
