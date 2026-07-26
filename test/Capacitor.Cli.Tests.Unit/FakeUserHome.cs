namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A disposable fake user home for one test, shared across the per-harness plugin test suites
/// (Cursor, Copilot, Gemini, …) so each doesn't re-derive path isolation.
///
/// Rooted UNDER the real user profile (<see cref="Environment.SpecialFolder.UserProfile"/>), because
/// <c>McpMarker</c> classifies a config as user-scope — writing its ownership marker as a sidecar next
/// to the config — only for configs under the profile. Rooting here keeps the common case a contained
/// sidecar on every OS (GetFolderPath(UserProfile) ignores $HOME on Windows, and a redirected TEMP can
/// sit outside the profile). The sidecar lives under <see cref="Path"/> and is removed with the
/// directory on dispose.
///
/// The central-marker case (the edge where the real profile is itself a git repo, so McpMarker treats
/// the config as non-user-scope and would write under <c>~/.kcap/mcp-markers</c>) no longer needs
/// per-test handling here: <see cref="McpMarkerGlobalSetup"/> pins McpMarker's central root to a
/// throwaway temp dir for the whole assembly, so NO test touches the real shared dir.
///
/// Suites using this must be <c>[NotInParallel("HomeEnvVarMutation")]</c> so the profile McpMarker
/// reads stays stable underneath the test.
/// </summary>
sealed class FakeUserHome : IDisposable {
    public string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $"kcap-test-home-{Guid.NewGuid().ToString("N")[..8]}");

    public FakeUserHome() => Directory.CreateDirectory(Path);

    public void Dispose() {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
