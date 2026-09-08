using System.ComponentModel;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>
/// Locates and spawns <c>gh</c>. A GUI app inherits launchd's PATH, which omits Homebrew and
/// user-local prefixes, so the login shell's PATH is searched first on macOS and Linux.
/// </summary>
public sealed class GitHubCliRunner(IProcessRunner runner, ILoginShellProbe? shell, Func<string, string?> getEnv) : IDisposable {
    public const int OutputLimit = 4 * 1024 * 1024;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    static readonly IReadOnlyDictionary<string, string> Overlay = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["GH_PROMPT_DISABLED"] = "1", ["GH_NO_UPDATE_NOTIFIER"] = "1", ["NO_COLOR"] = "1", ["GH_PAGER"] = "cat", ["CLICOLOR"] = "0",
    };
    readonly SemaphoreSlim _slots = new(2, 2);
    string? _path;

    public async Task<string?> LocateAsync(bool refresh, CancellationToken ct) {
        if (!refresh && _path is not null) return _path;
        string? found = null;
        if (shell is not null && !OperatingSystem.IsWindows() && await shell.TerminalPathAsync(ct).ConfigureAwait(false) is { } terminal)
            found = BinaryProbe.Searching(terminal).Resolve("gh");
        found ??= BinaryProbe.Searching(getEnv("PATH")).Resolve("gh");
        _path = found;
        return found;
    }

    public async Task<GitHubCliResult> RunAsync(string[] args, CancellationToken ct) {
        var path = _path ?? await LocateAsync(false, ct).ConfigureAwait(false);
        if (path is null) return new(GitHubCliOutcome.NotStarted, -1, "", "gh is not installed");
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try {
            ProcessResult result;
            try {
                result = await runner.RunAsync(path, args, new RunOptions(Overlay, Deadline, CancelMode.KillTree), ct).ConfigureAwait(false);
            } catch (Exception exception) when (exception is InvalidOperationException or IOException or Win32Exception) {
                _path = null;
                return new(GitHubCliOutcome.NotStarted, -1, "", exception.Message);
            }
            if (result.TimedOut) return new(GitHubCliOutcome.TimedOut, result.ExitCode, "", result.Stderr);
            if (result.Stdout.Length > OutputLimit) return new(GitHubCliOutcome.Oversized, result.ExitCode, "", "");
            return new(result.ExitCode == 0 ? GitHubCliOutcome.Ok : GitHubCliOutcome.Failed, result.ExitCode, result.Stdout, result.Stderr);
        } finally { _slots.Release(); }
    }

    public static bool ValidHost(string? host) => host is { Length: > 0 and <= 253 } && !host.Contains('/') && Uri.CheckHostName(host) == UriHostNameType.Dns;
    public static bool ValidOwner(string? owner) => owner is { Length: > 0 and <= 39 } && owner[0] != '-' && owner.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    public static bool ValidRepo(string? repo) => repo is { Length: > 0 and <= 100 } && repo is not ("." or "..")
        && repo.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    public static bool ValidNumber(int number) => number > 0;
    public static bool ValidBranch(string? branch) => branch is { Length: > 0 and <= 256 } && branch[0] is not ('-' or '/') && !branch.EndsWith('/')
        && !branch.EndsWith(".lock", StringComparison.Ordinal) && !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("@{", StringComparison.Ordinal)
        && !branch.Contains("//", StringComparison.Ordinal)
        && branch.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not ('~' or '^' or ':' or '?' or '*' or '[' or '\\'));
    public static bool ValidNodeId(string? id) => id is { Length: > 0 and <= 256 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '=' or '-');
    public static bool ValidCursor(string? cursor) => cursor is { Length: > 0 and <= 512 } && cursor.All(c => c is >= '!' and <= '~');

    public void Dispose() => _slots.Dispose();
}
