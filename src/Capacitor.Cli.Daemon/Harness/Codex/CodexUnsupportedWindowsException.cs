namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Thrown by <see cref="CodexLauncher"/>'s Prepare preflight when the host is a Windows build
/// older than 10.0.17763 (Windows 10 1809), below which Codex does not support its Windows
/// sandbox. The orchestrator catches this type and emits <c>LaunchFailed</c> with the
/// exception's message, so the user gets the version requirement and the doc link rather than
/// an opaque spawn failure.
///
/// <para>Normally unreachable from the dashboard: <see cref="CodexLauncher.IsAvailable"/>
/// applies the same gate, so an unsupported host never advertises the <c>codex</c> vendor in
/// the first place. This covers the race where a launch command was already in flight (or the
/// dashboard held a stale vendor list).</para>
/// </summary>
internal sealed class CodexUnsupportedWindowsException(string message) : Exception(message);
