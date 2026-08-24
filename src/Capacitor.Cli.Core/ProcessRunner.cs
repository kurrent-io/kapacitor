using System.Diagnostics;

namespace Capacitor.Cli.Core;

/// AbandonWait: an external ct cancellation abandons the WAIT only, child keeps running.
/// KillTree: an external ct cancellation kills the child (tree) and awaits its exit first.
public enum CancelMode {
    AbandonWait,
    KillTree,
}

/// Scope of the kill on internal Timeout expiry only — CancelMode's KillTree always kills the tree.
public enum TimeoutKillScope {
    Tree,
    ProcessOnly,
}

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public sealed record RunOptions(
    IReadOnlyDictionary<string, string>? EnvOverlay = null, // adds/overrides; rest of env untouched
    TimeSpan? Timeout = null,                                // internal deadline: kills per TimeoutKill scope + awaits on expiry
    CancelMode CancelMode = CancelMode.AbandonWait,
    TimeoutKillScope TimeoutKill = TimeoutKillScope.Tree);

public enum ProcessStreamKind { Stdout, Stderr }

public sealed record StreamedLine(ProcessStreamKind Kind, string Text);

/// No full Stdout/Stderr captures by design — Tail is only a bounded trailing window.
public sealed record StreamingResult(int ExitCode, bool TimedOut, IReadOnlyList<StreamedLine> Tail);

/// Seam over process spawning so process-driven services are testable without touching a real
/// CLI binary. The production implementation (<see cref="ProcessRunner"/>) wraps
/// System.Diagnostics.Process.
public interface IProcessRunner {
    Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct);

    /// Cancellation always kills the tree and awaits exit first — unlike RunAsync, ignores RunOptions.CancelMode.
    Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
        Action<StreamedLine> onLine, CancellationToken ct);
}

/// Production IProcessRunner: wraps System.Diagnostics.Process with stdout/stderr capture, an
/// env overlay, an internal timeout, and a per-call cancel mode. <c>RunOptions.Timeout</c> is an
/// internal deadline distinct from <c>ct</c>: on expiry the process (or tree, per
/// <c>RunOptions.TimeoutKill</c>) is killed and awaited, and the result comes back with
/// TimedOut=true rather than throwing. <c>ct</c> cancellation behaves per
/// <c>RunOptions.CancelMode</c>: AbandonWait abandons the WAIT only (a detached <c>daemon start
/// -d</c> keeps running) and still throws OperationCanceledException; KillTree kills the tree and
/// awaits its exit first, then STILL throws — cancellation is cancellation, TimedOut is only for
/// the internal Timeout.
public sealed class ProcessRunner : IProcessRunner {
    public async Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
        var psi = new ProcessStartInfo(fileName) {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (options.EnvOverlay is not null)
            foreach (var (key, value) in options.EnvOverlay) psi.Environment[key] = value;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        // CancellationToken.None on both drains: neither `ct` nor the internal timeout ever
        // abandons the pipes — a drain tied to either would stop reading on cancellation and
        // let a detached/killed child block on a full pipe buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = options.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var waitCts = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            await process.WaitForExitAsync(waitCts?.Token ?? ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            if (ct.IsCancellationRequested) {
                if (options.CancelMode == CancelMode.KillTree)
                    await KillAndAwaitAsync(process).ConfigureAwait(false);

                // The drains outlive this method on the abandoned-wait path — observe them
                // so a later fault surfaces nowhere instead of as an unobserved task
                // exception. Under KillTree the child is already dead, so this still
                // completes promptly; it just isn't awaited before the throw below.
                Observe(stdoutTask);
                Observe(stderrTask);
                throw;
            }

            // Only the internal timeout could have fired the linked token.
            await KillAndAwaitAsync(process, options.TimeoutKill == TimeoutKillScope.Tree).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: true);
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false);
    }

    const int TailLimit = 500;

    public async Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) {
        var psi = new ProcessStartInfo(fileName) {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (options.EnvOverlay is not null)
            foreach (var (key, value) in options.EnvOverlay) psi.Environment[key] = value;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var tailLock = new object();
        var tail = new Queue<StreamedLine>(TailLimit + 1);

        void Record(StreamedLine line) {
            try { onLine(line); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: streaming callback threw for '{fileName}': {ex}"); }

            lock (tailLock) {
                tail.Enqueue(line);
                if (tail.Count > TailLimit) tail.Dequeue();
            }
        }

        // Line-buffered per stream — no cross-stream ordering promise; drains to EOF even under kill.
        async Task PumpAsync(TextReader reader, ProcessStreamKind kind) {
            string? line;
            while ((line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
                Record(new StreamedLine(kind, line));
        }

        var stdoutTask = PumpAsync(process.StandardOutput, ProcessStreamKind.Stdout);
        var stderrTask = PumpAsync(process.StandardError, ProcessStreamKind.Stderr);

        using var timeoutCts = options.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var waitCts = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            await process.WaitForExitAsync(waitCts?.Token ?? ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            if (ct.IsCancellationRequested) {
                // Streaming always kills the tree on cancellation, ignoring RunOptions.CancelMode.
                await KillAndAwaitAsync(process).ConfigureAwait(false);
                // Awaited, not fire-and-forget: the pumps end at EOF once the tree is killed
                // (same as the timeout arm below) — a fire-and-forget Observe let a callback
                // fire AFTER this method had already thrown OCE, racing caller cleanup.
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }

            await KillAndAwaitAsync(process, options.TimeoutKill == TimeoutKillScope.Tree).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            lock (tailLock) return new StreamingResult(process.ExitCode, TimedOut: true, tail.ToArray());
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        lock (tailLock) return new StreamingResult(process.ExitCode, TimedOut: false, tail.ToArray());
    }

    static async Task KillAndAwaitAsync(Process process, bool entireProcessTree = true) {
        try { process.Kill(entireProcessTree); }
        catch (InvalidOperationException) { /* already exited */ }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
