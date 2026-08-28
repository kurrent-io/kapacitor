namespace Capacitor.Cli.Core;

/// <summary>
/// Reads that never deny Write to another handle. <c>File.ReadAllText</c>/<c>ReadLines</c> open
/// <see cref="FileShare.Read"/>, which on Windows is mandatory sharing and locks a coding agent out
/// of the file it owns — its transcript, its <c>{id}.json</c> sidecar, its own config. Worst on the
/// shutdown final drain, when the agent is flushing its last records. Invisible on macOS/Linux, so
/// only the Windows leg reddens.
/// </summary>
public static class SharedFileText {
    // FileShare.Delete too: a concurrent atomic replace (write-temp + File.Move overwrite) of the file
    // being read needs delete/rename sharing on our handle, or the move fails with a sharing violation
    // on Windows — which for the offer ledger silently drops a dismissal.
    const FileShare Sharing = FileShare.ReadWrite | FileShare.Delete;

    extension(File) {
        /// <summary>Whole file, sharing read+write+delete.</summary>
        public static string ReadAllTextShared(string path) {
            using var reader = OpenShared(path);

            return reader.ReadToEnd();
        }

        /// <summary>Asynchronous <see cref="ReadAllTextShared"/>.</summary>
        public static async Task<string> ReadAllTextSharedAsync(string path) {
            using var reader = OpenShared(path);

            return await reader.ReadToEndAsync();
        }

        /// <summary>Streamed sibling of <see cref="ReadAllTextShared"/>, for a scan that wants to stop
        /// as soon as it finds what it needs rather than materialize the file.</summary>
        public static IEnumerable<string> ReadLinesShared(string path) {
            using var reader = OpenShared(path);

            while (reader.ReadLine() is { } line) yield return line;
        }
    }

    static StreamReader OpenShared(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, Sharing));
}
