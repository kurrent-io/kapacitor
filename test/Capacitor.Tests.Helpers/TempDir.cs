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

    /// <summary>Creates a directory (and any missing parents) and returns its path.</summary>
    public string CreateDir(params ReadOnlySpan<string> segments) =>
        Directory.CreateDirectory(PathTo(segments)).FullName;

    /// <summary>Writes a file (creating any missing parent directories) and returns its path.</summary>
    public string CreateFile(string relativePath, string content = "") {
        var path = PathTo(relativePath);
        var dir  = System.IO.Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);

        return path;
    }

    /// <summary>As <see cref="CreateFile(string,string)"/>, under a random name, for tests that
    /// need only that <em>some</em> file exists.</summary>
    public string CreateFile() => CreateFile(System.IO.Path.GetRandomFileName());

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
