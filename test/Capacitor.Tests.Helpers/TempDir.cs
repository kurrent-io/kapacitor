using System.Runtime.CompilerServices;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// A throwaway directory under the system temp dir, deleted (best effort) on dispose.
///
/// <para>The directory is named <c>kcap-test-{caller's file}-{random}</c>: every test suite's
/// scratch space is greppable under one prefix, and a directory left behind by a crashed or
/// killed run names the suite that leaked it.</para>
/// </summary>
public sealed class TempDir : IDisposable {
    public string Path { get; }

    public TempDir([CallerFilePath] string callerFilePath = "") =>
        Path = Directory.CreateTempSubdirectory(Prefix(callerFilePath)).FullName;

    /// <summary>Path of an entry under this directory, from its path segments. Nothing is created —
    /// for a file the code under test is expected to create itself, or must find absent.</summary>
    public string PathTo(params ReadOnlySpan<string> segments) {
        var parts = new string[segments.Length + 1];

        parts[0] = Path;
        segments.CopyTo(parts.AsSpan(1));

        return System.IO.Path.Combine(parts);
    }

    /// <summary>A temp dir together with the path of one entry under it — for the common case of a
    /// test that needs a single absent path and never refers to the directory again:
    /// <c>using var tmp = TempDir.WithPathTo("config.json", out var path);</c>. Nothing is created.</summary>
    // callerFilePath is forwarded, not re-captured: taking the ctor's default here would name every
    // such directory after this file instead of the calling suite.
    public static TempDir WithPathTo(string relativePath, out string path, [CallerFilePath] string callerFilePath = "") {
        var dir = new TempDir(callerFilePath);
        path = dir.PathTo(relativePath);
        return dir;
    }

    /// <summary>Creates a directory (and any missing parents) and returns its path.</summary>
    public string CreateDir(params ReadOnlySpan<string> segments) {
        var path = PathTo(segments);
        return Directory.CreateDirectory(path).FullName;
    }

    /// <summary>Writes a file (creating any missing parent directories) and returns its path.</summary>
    public string CreateFile(string relativePath, string content = "") =>
        Write(PathTo(relativePath), content);

    /// <summary>As <see cref="CreateFile(string,string)"/>, from path segments — so a nested file
    /// needs no <c>Path.Combine</c> at the call site:
    /// <c>tmp.CreateFile(["events", "events.jsonl"], body)</c>.</summary>
    public string CreateFile(ReadOnlySpan<string> segments, string content = "") =>
        Write(PathTo(segments), content);

    /// <summary>As <see cref="CreateFile(string,string)"/> for line-oriented content:
    /// <c>tmp.CreateFile("events.jsonl", [lineA, lineB])</c>.</summary>
    // File.WriteAllLines, not a join: it terminates the LAST line too, and the JSONL fixtures here
    // are parsed by production readers that treat a final unterminated line as incomplete.
    public string CreateFile(string relativePath, string[] lines) {
        var path = PathTo(relativePath);

        EnsureParent(path);
        File.WriteAllLines(path, lines);

        return path;
    }

    static string Write(string path, string content) {
        EnsureParent(path);
        File.WriteAllText(path, content);

        return path;
    }

    static void EnsureParent(string path) {
        var dir = System.IO.Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public void Dispose() {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }

    // Windows tests create nested content under here, so the hint must not eat the path budget.
    const int MaxHintLength = 20;

    static string Prefix(string callerFilePath) {
        var stem = System.IO.Path.GetFileNameWithoutExtension(callerFilePath);

        if (stem.EndsWith("Tests", StringComparison.Ordinal)) stem = stem[..^5];

        var hint = new string(stem.Where(char.IsAsciiLetterOrDigit).Take(MaxHintLength).ToArray())
            .ToLowerInvariant();

        return hint.Length == 0 ? "kcap-test-" : $"kcap-test-{hint}-";
    }
}
