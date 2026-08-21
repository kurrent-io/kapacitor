using System.Runtime.CompilerServices;
using Capacitor.Cli.Core;

namespace Capacitor.Tests.Helpers;

/// <summary>A <see cref="DaemonStore"/> over a throwaway directory, deleted on dispose — the
/// isolation unit for anything touching daemon lock/pid/marker/socket files.</summary>
public sealed class TempDaemonStore : IDisposable {
    // A control socket binds in here and macOS caps sockaddr_un at 103 chars, so the name needs room.
    const int HintLength = 6;

    readonly TempDir _dir;

    public DaemonStore Store { get; }

    public string Directory => Store.Directory;

    /// <param name="hint">Names the directory instead of the caller's file.</param>
    public TempDaemonStore(string? hint = null, [CallerFilePath] string callerFilePath = "") {
        _dir  = new TempDir(Cut(hint ?? callerFilePath));
        Store = new DaemonStore(_dir.Path);
    }

    /// <inheritdoc cref="TempDir.PathTo"/>
    public string PathTo(params ReadOnlySpan<string> segments) => _dir.PathTo(segments);

    /// <inheritdoc cref="TempDir.CreateDir"/>
    public TempDirHandle CreateDir(params ReadOnlySpan<string> segments) => _dir.CreateDir(segments);

    /// <inheritdoc cref="TempDir.CreateFile(string,string)"/>
    public string CreateFile(string relativePath, string content = "") => _dir.CreateFile(relativePath, content);

    public void Dispose() => _dir.Dispose();

    // Enough of the suite name to attribute a leak, within the socket budget.
    static string Cut(string fileOrClassName) =>
        new(TempDir.Stem(fileOrClassName).Where(char.IsAsciiLetterOrDigit).Take(HintLength).ToArray());
}
