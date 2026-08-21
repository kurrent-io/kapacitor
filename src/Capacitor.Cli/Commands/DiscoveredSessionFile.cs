namespace Capacitor.Cli.Commands;

/// <summary>Shared file facts a source may need when dating a discovered session.</summary>
internal static class DiscoveredSessionFile {
    /// <summary>The <c>FilePath</c> a file-based source records in its <c>SourceMeta</c>, or null.</summary>
    public static string? PathOf(DiscoveredSession session) =>
        session.SourceMeta.TryGetValue("FilePath", out var raw) ? raw as string : null;

    /// <summary>The file's last write, or null when it cannot be read.</summary>
    public static DateTimeOffset? LastWrite(string? filePath) {
        if (filePath is null) return null;

        try { return File.GetLastWriteTimeUtc(filePath); } catch { return null; }
    }
}
