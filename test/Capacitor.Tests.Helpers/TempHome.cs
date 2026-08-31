using System.Runtime.CompilerServices;
using Capacitor.Cli.Core;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// A <see cref="UserHome"/> over a throwaway directory, deleted on dispose — the isolation unit for
/// anything that resolves a path under the user's home. A directory acting like a home is enough:
/// nothing under test asks the OS who the user is, it joins paths under the root it was handed.
///
/// <para>The path is symlink-resolved, unlike <see cref="TempConfigRoot"/>'s.
/// <c>CodexConfigToml</c>'s guard refuses a symlinked component and a Mac's temp root is
/// <c>/var</c> → <c>/private</c>, so an unresolved home makes a Codex registration return
/// <c>Failed</c> and write nothing — on macOS only, which no CI leg covers.</para>
/// </summary>
public sealed class TempHome : IDisposable {
    readonly TempDir _dir;

    /// <summary>The home directory, symlink-resolved.</summary>
    public string Path { get; }

    public UserHome Home { get; }

    /// <summary>So an injected fixture reads as the value it stands for at a call site expecting a
    /// <see cref="UserHome"/> — as <see cref="TempDirHandle"/> does for a path.</summary>
    public static implicit operator UserHome(TempHome home) => home.Home;

    /// <param name="hint">Names the directory instead of the caller's file.</param>
    public TempHome(string? hint = null, [CallerFilePath] string callerFilePath = "") {
        _dir = new TempDir(TempDir.Stem(hint ?? callerFilePath));
        Path = _dir.GetResolvedPath();
        Home = new UserHome(Path);
    }

    // Every member resolves under the resolved root, so a path the test builds is the one the code
    // under test sees.
    TempDirHandle Root => new(Path);

    /// <inheritdoc cref="TempDirHandle.PathTo"/>
    public string PathTo(params ReadOnlySpan<string> segments) => Root.PathTo(segments);

    /// <inheritdoc cref="TempDirHandle.CreateDir"/>
    public TempDirHandle CreateDir(params ReadOnlySpan<string> segments) => Root.CreateDir(segments);

    /// <inheritdoc cref="TempDirHandle.CreateFile(string,string)"/>
    public string CreateFile(string relativePath, string content = "") =>
        Root.CreateFile(relativePath, content);

    /// <inheritdoc cref="TempDirHandle.CreateFile(ReadOnlySpan{string},string)"/>
    public string CreateFile(ReadOnlySpan<string> segments, string content = "") =>
        Root.CreateFile(segments, content);

    public void Dispose() => _dir.Dispose();
}
