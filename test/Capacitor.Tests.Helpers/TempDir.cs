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

    public TempDir([CallerFilePath] string callerFilePath = "") {
        Path = Directory.CreateTempSubdirectory(Prefix(callerFilePath)).FullName;
    }

    // Every path/file operation lives on TempDirHandle and is reached through here, so the two types
    // cannot drift apart. TempDir adds only ownership: the temp root, and deleting it on dispose.
    TempDirHandle Root => new(Path);

    /// <summary>Path of an entry under this directory, from its path segments. Nothing is created —
    /// for a file the code under test is expected to create itself, or must find absent.</summary>
    public string PathTo(params ReadOnlySpan<string> segments) => Root.PathTo(segments);

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

    /// <summary>Creates a directory (and any missing parents) and returns it. The result converts
    /// implicitly to its path, and can make files and further directories under itself.</summary>
    public TempDirHandle CreateDir(params ReadOnlySpan<string> segments) => Root.CreateDir(segments);

    /// <inheritdoc cref="TempDirHandle.CreateFile(string,string)"/>
    public string CreateFile(string relativePath, string content = "") =>
        Root.CreateFile(relativePath, content);

    /// <inheritdoc cref="TempDirHandle.CreateFile(ReadOnlySpan{string},string)"/>
    public string CreateFile(ReadOnlySpan<string> segments, string content = "") =>
        Root.CreateFile(segments, content);

    /// <inheritdoc cref="TempDirHandle.CreateFile(string,string[])"/>
    public string CreateFile(string relativePath, string[] lines) =>
        Root.CreateFile(relativePath, lines);

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
