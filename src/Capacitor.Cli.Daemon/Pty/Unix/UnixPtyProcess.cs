using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Pty.Unix;

public sealed class UnixPtyProcess : IPtyProcess {
    readonly int                     _masterFd;
    readonly CancellationTokenSource _cts = new();
    bool                             _disposed;

    public int     Pid           { get; }
    public bool    HasExited     { get; private set; }
    public int?    ExitCode      { get; private set; }
    public string? StartIdentity { get; } // never null on Unix: "" (uncapturable) or a real token

    UnixPtyProcess(int masterFd, int childPid, string startIdentity) {
        _masterFd     = masterFd;
        Pid           = childPid;
        StartIdentity = startIdentity;
    }

    /// <summary>Executable resolution is PRE-FORK, in the parent (spec §4.2(a), pinned): resolves
    /// <paramref name="command"/> against the SAME env/PATH that will be passed to the child
    /// (never the daemon's ambient PATH if extraEnv overrides it — closes the execvpe "resolve
    /// against the wrong PATH" trap at the top level too, matching the shim's own env-shebang
    /// resolution rule). Mirrors POSIX execvp semantics: an absolute path is used as-is; a path
    /// containing '/' is resolved against <paramref name="cwd"/> (matching execvp after the OLD
    /// managed branch's <c>chdir(cwd)</c> ran before <c>execvp</c> — a relative path with a slash
    /// must resolve the same way now that resolution happens pre-fork/pre-chdir); a bare name is
    /// searched on PATH.</summary>
    internal static string ResolveExecutableAbsolutePath(string command, string cwd, IReadOnlyDictionary<string, string> childEnv) {
        if (Path.IsPathRooted(command)) return command;
        if (command.Contains('/')) return Path.GetFullPath(command, cwd);

        var path = childEnv.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH") ?? "";
        // Split with StringSplitOptions.None (NOT RemoveEmptyEntries): POSIX treats an EMPTY
        // PATH field (a leading/trailing ':' or an internal '::') as the current directory, and
        // the native child does chdir(cwd) before exec — so an empty field, and any relative
        // field, must resolve against `cwd`, matching exec-time resolution rather than dropping
        // the field. RemoveEmptyEntries silently discarded exactly those cwd fields, so a
        // command that only lives in cwd would have gone unfound here while the child would have
        // exec'd it fine.
        foreach (var entry in path.Split(':', StringSplitOptions.None)) {
            var dir = entry.Length == 0            ? cwd
                    : Path.IsPathRooted(entry)     ? entry
                    :                                Path.GetFullPath(entry, cwd);
            var candidate = Path.GetFullPath(Path.Combine(dir, command));
            // execvp selects the first EXECUTABLE file, not the first that merely EXISTS. A
            // non-executable file earlier on PATH must be SKIPPED here, or we'd preflight (and
            // hand the child) a different inode than the one the child's own exec-time resolution
            // would land on — a non-executable match is invisible to execvp, which keeps scanning.
            if (IsExecutableRegularFile(candidate)) return candidate;
        }

        throw new InvalidOperationException($"'{command}' not found on PATH");
    }

    /// <summary>True iff <paramref name="path"/> is an existing regular file that <c>access(X_OK)</c>
    /// accepts — matching execvp, which selects the first PATH candidate it can ACTUALLY execute.
    /// "Any execute bit set" is NOT that test: a daemon-owned file with mode 0010 carries a group
    /// execute bit its owner can't use, and ACLs / noexec mounts diverge from the raw mode bits too,
    /// so a bit-mask check could select an earlier candidate the child then fails to exec instead of
    /// continuing down PATH the way execvp does. Any <c>access</c>/stat error (file gone, unreadable,
    /// wrong permission class, noexec mount) is treated as a SKIPPED candidate — return false and let
    /// the caller fall through to the next PATH entry. Unix-only (this whole resolver is the Unix PTY
    /// path; Windows uses the ConPty resolver).</summary>
    static bool IsExecutableRegularFile(string path) {
        // Retain the regular-file requirement: access(X_OK) also succeeds on a SEARCHABLE directory
        // (X on a dir means "search"), which execvp rejects — File.Exists is false for directories,
        // matching S_ISREG. Then apply execvp's own permission-class-aware executability test; 0 ==
        // accessible, any error (EACCES wrong class / noexec mount, ENOENT raced-away, …) → skip.
        if (!File.Exists(path)) return false;
        return UnixPtyInterop.access(path, UnixPtyInterop.X_OK) == 0;
    }

    static IReadOnlyDictionary<string, string> BuildChildEnv(Dictionary<string, string>? extraEnv, ushort cols, ushort rows) {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry is { Key: string key, Value: string value }) env[key] = value;

        env["TERM"]    = "xterm-256color";
        env["LANG"]    = "en_US.UTF-8";
        env["COLUMNS"] = cols.ToString();
        env["LINES"]   = rows.ToString();

        // Clear any hosted-agent identity/routing (and Claude session vars / daemon supervision
        // state) the daemon may have inherited BEFORE re-applying extraEnv — matches the OLD
        // managed child branch's ordering (unsetenv, then extraEnv setenv) exactly.
        foreach (var key in PtyEnvScrub.ClaudeSessionVars) env.Remove(key);
        foreach (var key in PtyEnvScrub.HostedAgentVars) env.Remove(key);
        foreach (var key in PtyEnvScrub.DaemonSupervisionVars) env.Remove(key);

        if (extraEnv is not null)
            foreach (var (key, value) in extraEnv) env[key] = value;

        return env;
    }

    static string?[] ToEnvpArray(IReadOnlyDictionary<string, string> env) {
        var arr = new string?[env.Count + 1];
        var i = 0;
        foreach (var (k, v) in env) arr[i++] = $"{k}={v}";
        arr[i] = null;
        return arr;
    }

    // A pure capability probe (kernel version gate) — safe to cache per-process regardless of
    // who owns the spawner thread's lifetime.
    static readonly Lazy<int> ExecveatSupported = new(UnixPtyInterop.pty_probe_execveat);

    /// <summary>The dedicated spawner thread is DI-owned (see <see cref="UnixPtyProcessFactory"/>
    /// and <c>DaemonRunner</c>'s registration), never a static/process-wide singleton constructed
    /// here: <see cref="UnixSpawnerThread"/> starts a non-background (<c>IsBackground = false</c>)
    /// OS thread, and a foreground thread that nobody ever disposes keeps the WHOLE process alive
    /// past the end of <c>Main</c> — confirmed empirically while building this (a static
    /// self-constructing <c>Lazy&lt;UnixSpawnerThread&gt;</c> here hung the test host indefinitely,
    /// since nothing outside this method could ever reach in and call <c>Dispose()</c> on it).
    /// Passing it in lets every caller (the daemon's DI container at normal shutdown; a test that
    /// owns and disposes its own instance) control the thread's lifetime explicitly.</summary>
    public static UnixPtyProcess Spawn(
            UnixSpawnerThread           spawner,
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        ) {
        var childEnv = BuildChildEnv(extraEnv, cols, rows);
        var envpArr  = ToEnvpArray(childEnv);

        var resolvedPath = ResolveExecutableAbsolutePath(command, cwd, childEnv);

        var origArgv = new string?[args.Length + 2];
        origArgv[0] = command; // argv[0] stays the ORIGINAL (possibly unresolved) command name
        Array.Copy(args, 0, origArgv, 1, args.Length);
        origArgv[^1] = null;

        var rc = UnixPtyInterop.pty_preflight(resolvedPath, origArgv, envpArr, ExecveatSupported.Value, out var plan);
        if (rc != 0) {
            throw new InvalidOperationException($"pty_preflight failed for '{resolvedPath}' — the executable could not be resolved");
        }

        if (UnixPtyInterop.pty_plan_contained(plan) == 0) {
            Console.Error.WriteLine($"[kcap] warning: launch of '{resolvedPath}' is UNCONTAINED (privileged binary, unreadable/inspection-failed preflight, pre-3.19 kernel, or a multi-token/unresolvable shebang) — falling back to the managed record/scan reap layers only.");
        }

        try {
            var result = spawner.SpawnOn(plan, envpArr, cwd, rows, cols, Environment.ProcessId, cancelFd: -1);

            if (result.FailedStep != 0) {
                throw new InvalidOperationException(
                    $"pty_spawn failed: step {result.FailedStep}, errno {result.ErrNo}");
            }

            return new UnixPtyProcess(result.MasterFd, result.Pid, result.StartIdentityString);
        } finally {
            UnixPtyInterop.pty_plan_free(ref plan); // the plan is spent whether spawn succeeded or failed
        }
    }

    public async IAsyncEnumerable<byte[]> ReadOutputAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        ) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var       buf    = new byte[4096];

        while (!linked.Token.IsCancellationRequested && !HasExited) {
            var bytesRead = await Task.Run(
                () => {
                    var pfd = new UnixPtyInterop.PollFd { fd = _masterFd, events = UnixPtyInterop.POLLIN };

                    while (!linked.Token.IsCancellationRequested) {
                        var pollResult = UnixPtyInterop.poll(ref pfd, 1, 200);

                        switch (pollResult) {
                            case > 0 when (pfd.revents & UnixPtyInterop.POLLIN) != 0: {
                                var n = UnixPtyInterop.read(_masterFd, buf, buf.Length);

                                return (int)n;
                            }
                            case > 0 when (pfd.revents & (UnixPtyInterop.POLLHUP | UnixPtyInterop.POLLERR)) != 0:
                                // Slave side closed or errored without buffered data — EOF
                                return 0;
                            case < 0:
                                return -1;
                        }
                    }

                    return 0;
                },
                CancellationToken.None
            );

            if (bytesRead <= 0) {
                CheckExited();

                yield break;
            }

            var data = new byte[bytesRead];
            Array.Copy(buf, data, bytesRead);

            yield return data;
        }
    }

    public Task WriteAsync(string input) {
        var bytes = Encoding.UTF8.GetBytes(input);

        return Task.Run(() => UnixPtyInterop.write(_masterFd, bytes, bytes.Length));
    }

    public Task WriteAsync(byte[] data) {
        return Task.Run(() => UnixPtyInterop.write(_masterFd, data, data.Length));
    }

    public void Resize(ushort cols, ushort rows) {
        UnixPtyInterop.SetWinSize(_masterFd, rows, cols);
    }

    public void SendInterrupt() {
        if (!HasExited) {
            UnixPtyInterop.kill(Pid, UnixPtyInterop.SIGINT);
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        if (HasExited) {
            return;
        }

        SignalGroup(UnixPtyInterop.SIGTERM);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (!HasExited && DateTime.UtcNow < deadline) {
            CheckExited();
            if (!HasExited) {
                await Task.Delay(100);
            }
        }

        if (!HasExited) {
            SignalGroup(UnixPtyInterop.SIGKILL);
            CheckExited();
        }
    }

    /// <summary>Signals the child's process GROUP, falling back to the pid alone (ProcessReaper's
    /// pattern). The child is a forkpty session leader (pgid == pid), so helpers it spawned —
    /// codex's code-mode host, MCP servers — are in the group; signalling only the leader orphans
    /// them whenever the leader dies without forwarding (SIGKILL always, SIGTERM vendor-dependent).
    /// Safe against pid reuse: callers only signal while <see cref="HasExited"/> is false, and the
    /// leader is not reaped until <see cref="CheckExited"/>'s waitpid, so the pgid still names our
    /// lineage. Once the leader is gone the group id proves nothing and is never signalled — a
    /// descendant that outlived the group kill is the record/scan reap layers' job.</summary>
    void SignalGroup(int sig) {
        if (UnixPtyInterop.kill(-Pid, sig) != 0) UnixPtyInterop.kill(Pid, sig);
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        if (HasExited) {
            return;
        }

        var sw    = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);

        while (!HasExited && sw.Elapsed < limit) {
            CheckExited();

            if (!HasExited) {
                await Task.Delay(50);
            }
        }
    }

    void CheckExited() {
        var result = UnixPtyInterop.waitpid(Pid, out var status, UnixPtyInterop.WNOHANG);

        if (result == Pid) {
            HasExited = true;
            ExitCode  = (status >> 8) & 0xFF;
        }
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) {
            return;
        }

        _disposed = true;

        await _cts.CancelAsync();

        if (!HasExited) {
            await TerminateAsync();
        }

        UnixPtyInterop.close(_masterFd);
        _cts.Dispose();
    }
}

public class UnixPtyProcessFactory(UnixSpawnerThread spawner) : IPtyProcessFactory {
    public IPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        )
        => UnixPtyProcess.Spawn(spawner, command, args, cwd, extraEnv, cols, rows);
}
