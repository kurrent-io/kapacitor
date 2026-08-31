using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Tests.Helpers;

/// <summary>The outcome of one git invocation: exit code and both streams, drained.</summary>
public readonly record struct GitResult(int ExitCode, string StdOut, string StdErr) {
    /// <summary>Stdout without its trailing newline — what a capture almost always wants.</summary>
    public string Text => StdOut.TrimEnd();

    public override string ToString() => Text;
}

/// <summary>
/// A real git repository to run git against.
///
/// <para><see cref="Create"/> owns a throwaway directory and deletes it on dispose.
/// <see cref="InitIn"/> and <see cref="At"/> attach to a directory owned elsewhere, so disposing
/// one of those is a no-op by design — a linked worktree is removed with <c>git worktree remove</c>,
/// and a repository under another fixture's <see cref="TempDir"/> is not this object's to delete.</para>
///
/// <para>Hermetic with respect to the machine's git configuration: see
/// <c>Guards/GitConfigGlobalSetup</c>, which pins that for every git the assembly starts, including
/// production code's children.</para>
/// </summary>
public sealed class GitRepo : IDisposable {
    readonly TempDirHandle _dir;

    /// <summary>Null unless this instance created the directory. Ownership is what Dispose acts on.</summary>
    readonly TempDir? _owned;

    GitRepo(TempDirHandle dir, TempDir? owned) {
        _dir   = dir;
        _owned = owned;
    }

    public string Path => _dir.Path;

    /// <summary>Identity is repository-local because the assembly-wide config pin deliberately
    /// supplies none.</summary>
    const string AuthorName  = "Test";
    const string AuthorEmail = "test@example.com";


    /// <summary>A repository in a throwaway directory this instance owns and deletes.</summary>
    /// <param name="hint">Names the directory instead of the caller's file.</param>
    public static GitRepo Create(string? hint = null, [CallerFilePath] string callerFilePath = "") {
        var tmp  = new TempDir(hint, callerFilePath);
        var repo = new GitRepo(tmp.Root, tmp);

        // Dispose before rethrowing: a repository that never reaches its caller has no other owner.
        try { repo.Init(); } catch { repo.Dispose(); throw; }

        return repo;
    }

    /// <summary>A repository with one commit, for fixtures whose code path an empty (unborn-branch)
    /// repository would not exercise.</summary>
    public static GitRepo CreateWithCommit(string? hint = null, [CallerFilePath] string callerFilePath = "") {
        var repo = Create(hint, callerFilePath);

        try {
            repo.CreateFile("README.md", "test");
            repo.CommitAll("initial");
        } catch { repo.Dispose(); throw; }

        return repo;
    }

    /// <summary>Initialises a repository under a directory someone else owns, optionally at a
    /// sub-path — for a fixture whose position matters, such as a repository named by a relative
    /// symlink. Owns nothing, so disposing is a no-op. Otherwise identical to <see cref="Create"/>,
    /// which owns its directory instead.</summary>
    public static GitRepo InitIn(TempDirHandle dir, params ReadOnlySpan<string> segments) {
        var repo = new GitRepo(dir.CreateDir(segments), owned: null);

        repo.Init();

        return repo;
    }

    /// <summary>Attaches to a directory that is already a repository — typically one the code under
    /// test created. Runs no git.</summary>
    public static GitRepo At(string path) => new(new TempDirHandle(path), owned: null);

    void Init() {
        // -b main explicitly: the config pin leaves init.defaultBranch unset.
        Do("init", "-q", "-b", "main");
        SetIdentity();
    }

    void SetIdentity() {
        Do("config", "user.email", AuthorEmail);
        Do("config", "user.name", AuthorName);
    }

    public static implicit operator string(GitRepo repo) => repo.Path;

    public override string ToString() => Path;


    /// <summary>Runs git here, throwing on a non-zero exit.</summary>
    public GitResult Do(params string[] args) => Run(args, stdin: null, throwOnFailure: true);

    /// <summary>Runs git here and hands back the failure — for a test that asserts on one.</summary>
    public GitResult Try(params string[] args) => Run(args, stdin: null, throwOnFailure: false);

    /// <summary>Runs git here feeding <paramref name="stdin"/>, throwing on a non-zero exit.</summary>
    public GitResult DoWithInput(byte[] stdin, params string[] args) =>
        Run(args, stdin, throwOnFailure: true);


    /// <summary>Stages <paramref name="paths"/>, or everything when given none.</summary>
    public GitResult Add(params string[] paths) =>
        paths.Length == 0 ? Do("add", "-A") : Do(["add", .. paths]);

    /// <summary>Commits what is staged. Reading the new sha is <see cref="Head"/>, kept separate so
    /// the common case does not pay for a second git invocation it discards.</summary>
    public GitResult Commit(string message) => Do("commit", "-q", "-m", message);

    /// <summary>Stages everything and commits it.</summary>
    public GitResult CommitAll(string message) {
        Add();

        return Commit(message);
    }

    public string Head => RevParse("HEAD");

    public string RevParse(string rev) => Do("rev-parse", rev).Text;

    public string Status => Do("status", "--porcelain").Text;

    public string CurrentBranch => Do("branch", "--show-current").Text;

    public GitResult Config(string key, string value) => Do("config", key, value);

    public GitResult AddRemote(string url, string name = "origin") => Do("remote", "add", name, url);

    public GitResult Checkout(string name, bool create = false) =>
        create ? Do("checkout", "-q", "-b", name) : Do("checkout", "-q", name);

    /// <summary>Adds a linked worktree and attaches to it.</summary>
    public GitRepo AddWorktree(string path, string? branch = null) {
        var destination = Resolve(path);

        _ = branch is null
            ? Do("worktree", "add", "-q", destination)
            : Do("worktree", "add", "-q", destination, "-b", branch);

        return At(destination);
    }

    /// <summary>Clones this repository and attaches to the clone. Identity is set on the clone too:
    /// it is a separate repository, so the source's local config does not reach it.</summary>
    public GitRepo Clone(string destination) {
        var full = Resolve(destination);

        Do("clone", "-q", Path, full);

        var clone = At(full);

        clone.SetIdentity();

        return clone;
    }

    /// <summary>A destination as git will read it — relative to this repository, since that is the
    /// working directory git runs in. The returned repository resolves its own paths against the
    /// test process instead, so handing it the caller's relative string would point it elsewhere.</summary>
    string Resolve(string destination) => System.IO.Path.GetFullPath(destination, Path);


    /// <inheritdoc cref="TempDirHandle.PathTo"/>
    public string PathTo(params ReadOnlySpan<string> segments) => _dir.PathTo(segments);

    /// <inheritdoc cref="TempDirHandle.CreateDir"/>
    public TempDirHandle CreateDir(params ReadOnlySpan<string> segments) => _dir.CreateDir(segments);

    /// <inheritdoc cref="TempDirHandle.CreateFile(string,string)"/>
    public string CreateFile(string relativePath, string content = "") =>
        _dir.CreateFile(relativePath, content);

    /// <inheritdoc cref="TempDirHandle.CreateFile(ReadOnlySpan{string},string)"/>
    public string CreateFile(ReadOnlySpan<string> segments, string content = "") =>
        _dir.CreateFile(segments, content);

    /// <inheritdoc cref="TempDirHandle.CreateFile(string,string[])"/>
    public string CreateFile(string relativePath, string[] lines) => _dir.CreateFile(relativePath, lines);

    public void Dispose() => _owned?.Dispose();


    GitResult Run(string[] args, byte[]? stdin, bool throwOnFailure) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = Path,
            RedirectStandardInput  = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        Process proc;

        try {
            proc = Process.Start(psi)!;
        } catch (Win32Exception ex) {
            throw new InvalidOperationException(StartFailureDiagnostic(args), ex);
        }

        using (proc) {
            // Drain both streams before WaitForExit: an unread redirected stream can fill its pipe
            // buffer and block the child forever, turning a failure into a silent hang.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (stdin is not null) {
                proc.StandardInput.BaseStream.Write(stdin);
                proc.StandardInput.Close();
            }

            proc.WaitForExit();

            var result = new GitResult(
                proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());

            return throwOnFailure && result.ExitCode != 0
                ? throw new InvalidOperationException(
                    $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr}")
                : result;
        }
    }

    /// <summary>On Unix, a missing working directory and a missing executable both surface as the
    /// same ENOENT, with .NET's message quoting the working directory either way. Capture both facts
    /// so a report can tell which one it was.</summary>
    string StartFailureDiagnostic(string[] args) =>
        $"Failed to start 'git {string.Join(' ', args)}'. " +
        $"WorkingDirectory '{Path}' exists: {Directory.Exists(Path)}. " +
        $"'git' startable from a known-good directory: {ProbeGitStartable()}. " +
        $"'git' resolves to: {BinaryProbe.FromEnvironment().Resolve("git") ?? "NOT FOUND"}. " +
        $"PATH={Environment.GetEnvironmentVariable("PATH")}";

    /// <summary>Answers "could this process start git at all?" by spawning <c>git --version</c> from a
    /// directory known to exist, rather than modelling executable resolution — a check that cannot be
    /// done correctly, since UseShellExecute=false skips .cmd/.bat shims and a Unix execute bit proves
    /// nothing about effective permission or PATH traversability. Splits the ENOENT ambiguity: startable
    /// here means the earlier fault was the working directory, not the executable.</summary>
    static string ProbeGitStartable() {
        // Bounded so the diagnostic itself can't wedge the run and produce no report at all.
        const int probeTimeoutMs = 10_000;

        // Must never throw (it runs inside a catch, so an escape would replace the message) and "NO"
        // must mean only "could not start" — a post-start failure keeps the YES verdict.
        Process? probe = null;

        try {
            try {
                probe = Process.Start(new ProcessStartInfo("git", "--version") {
                    WorkingDirectory       = AppContext.BaseDirectory, // exists; not the suspect dir
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                });
            } catch (Exception startEx) {
                return $"NO — {startEx.GetType().Name}: {startEx.Message}";
            }

            if (probe is null) return "NO — Process.Start returned null";

            // Startability is proven past here; anything below must keep the YES verdict.
            try {
                var versionTask = probe.StandardOutput.ReadToEndAsync();
                var errTask     = probe.StandardError.ReadToEndAsync();

                if (!probe.WaitForExit(probeTimeoutMs)) {
                    try { probe.Kill(entireProcessTree: true); } catch { /* best effort */ }

                    return $"YES (startable; probe did not exit within {probeTimeoutMs}ms, killed)";
                }

                var version = versionTask.GetAwaiter().GetResult().Trim();
                var err     = errTask.GetAwaiter().GetResult().Trim();

                return probe.ExitCode == 0
                    ? $"YES ({version})"
                    : $"YES (startable; --version exited {probe.ExitCode}: {err})";
            } catch (Exception afterStartEx) {
                return $"YES (startable; probe failed after starting — " +
                       $"{afterStartEx.GetType().Name}: {afterStartEx.Message})";
            }
        } finally {
            // Disposal must not be able to change the verdict or escape.
            try { probe?.Dispose(); } catch { /* best effort */ }
        }
    }
}
