namespace Capacitor.Cli.Core;

/// <summary>
/// Reads a file with <c>FileShare.ReadWrite</c> so the open never denies another handle's write.
/// Required for any file a coding agent owns and writes (its <c>settings.json</c>/<c>hooks.json</c>):
/// <c>File.ReadAllText</c> opens <c>FileShare.Read</c>, which on Windows is mandatory sharing and
/// blocks the agent writing its own config while we read. Invisible on macOS/Linux; only reddens
/// Windows. The Core counterpart of the CLI's <c>WatchCommand.ReadAllTextShared</c>.
/// </summary>
public static class SharedFileText {
    public static string ReadAllText(string path) {
        // FileShare.Delete too: a concurrent atomic replace (write-temp + File.Move overwrite) of the
        // file we're reading needs delete/rename sharing on our open handle, or the move fails with a
        // sharing violation on Windows — which for the offer ledger would silently drop a dismissal.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
