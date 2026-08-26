using System.Runtime.CompilerServices;
using Capacitor.Cli.Core;

namespace Capacitor.Tests.Helpers;

/// <summary>A <see cref="ConfigRoot"/> over a throwaway directory, deleted on dispose — the
/// isolation unit for anything reading or writing kcap's config directory. Because the root also
/// names the cross-process locks taken on files under it, a test holding one contends with nobody.</summary>
public sealed class TempConfigRoot : IDisposable {
    readonly TempDir _dir;

    public ConfigRoot Root { get; }

    public string Directory => Root.Directory;

    /// <param name="hint">Names the directory instead of the caller's file.</param>
    public TempConfigRoot(string? hint = null, [CallerFilePath] string callerFilePath = "") {
        _dir = new TempDir(TempDir.Stem(hint ?? callerFilePath));
        Root = new ConfigRoot(_dir.Path);
    }

    /// <inheritdoc cref="TempDir.PathTo"/>
    public string PathTo(params ReadOnlySpan<string> segments) => _dir.PathTo(segments);

    /// <inheritdoc cref="TempDir.CreateDir"/>
    public TempDirHandle CreateDir(params ReadOnlySpan<string> segments) => _dir.CreateDir(segments);

    /// <inheritdoc cref="TempDir.CreateFile(string,string)"/>
    public string CreateFile(string relativePath, string content = "") => _dir.CreateFile(relativePath, content);

    public void Dispose() => _dir.Dispose();
}
