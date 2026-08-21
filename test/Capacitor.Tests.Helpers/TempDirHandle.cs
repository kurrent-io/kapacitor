namespace Capacitor.Tests.Helpers;

/// <summary>
/// A directory that makes its own children — what <see cref="TempDir.CreateDir"/> returns. Converts
/// implicitly to its path, so it still goes anywhere the path string did. Owns nothing; the
/// <see cref="TempDir"/> it came from deletes the tree. The public constructor does not create.
///
/// <para>Pass <see cref="Path"/> explicitly to generic APIs: inference beats a user-defined conversion,
/// so <c>JsonValue.Create(dir)</c> compiles and serialises the struct instead of the path.</para>
/// </summary>
public readonly record struct TempDirHandle(string Path) {
    public static implicit operator string(TempDirHandle dir) => dir.Path;

    public override string ToString() => Path;

    /// <summary>Path of an entry under this directory. Nothing is created.</summary>
    public string PathTo(params ReadOnlySpan<string> segments) {
        var parts = new string[segments.Length + 1];

        parts[0] = Path;
        segments.CopyTo(parts.AsSpan(1));

        return System.IO.Path.Combine(parts);
    }

    /// <summary>Creates a subdirectory (and any missing parents) and returns it.</summary>
    public TempDirHandle CreateDir(params ReadOnlySpan<string> segments) =>
        new(Directory.CreateDirectory(PathTo(segments)).FullName);

    /// <summary>Writes a file, creating any missing parent directories, and returns its path.</summary>
    public string CreateFile(string relativePath, string content = "") =>
        Write(PathTo(relativePath), content);

    /// <summary>As <see cref="CreateFile(string,string)"/>, from path segments:
    /// <c>dir.CreateFile(["events", "events.jsonl"], body)</c>.</summary>
    public string CreateFile(ReadOnlySpan<string> segments, string content = "") =>
        Write(PathTo(segments), content);

    /// <summary>As <see cref="CreateFile(string,string)"/> for line-oriented content:
    /// <c>dir.CreateFile("events.jsonl", [lineA, lineB])</c>.</summary>
    // WriteAllLines, not a join: it terminates the last line, which the JSONL readers require.
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
}
