namespace Capacitor.Tests.Helpers;

/// <summary>
/// A directory that can make its own children — the return of <see cref="TempDir.CreateDir"/>.
///
/// <para>Converts implicitly to its path, so it goes wherever a directory string went before:
/// <c>Production(dir)</c>, <c>Path.Combine(dir, x)</c>, <c>File.Exists(dir)</c>. What it adds is the
/// half that used to be written by hand — <c>dir.CreateFile("a.json", body)</c> instead of a
/// <c>Path.Combine</c> plus a <c>File.WriteAllText</c>.</para>
///
/// <para>Owns nothing: the <see cref="TempDir"/> it came from deletes the whole tree. Constructing one
/// directly wraps an existing directory (a production-returned path, say) and does NOT create it —
/// only <see cref="CreateDir"/> creates.</para>
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

    /// <summary>As <see cref="CreateFile(string,string)"/>, from path segments — so a nested file needs
    /// no <c>Path.Combine</c> at the call site: <c>dir.CreateFile(["events", "events.jsonl"], body)</c>.</summary>
    public string CreateFile(ReadOnlySpan<string> segments, string content = "") =>
        Write(PathTo(segments), content);

    /// <summary>As <see cref="CreateFile(string,string)"/> for line-oriented content:
    /// <c>dir.CreateFile("events.jsonl", [lineA, lineB])</c>.</summary>
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
}
