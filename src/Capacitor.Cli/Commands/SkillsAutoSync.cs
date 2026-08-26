using System.Diagnostics;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Spawns the detached, self-throttling background skills refresh (<c>kcap skills sync --auto</c>).
/// Fire-and-forget by contract: the caller is a hook with a latency budget, so the child is never
/// awaited and every failure is swallowed — a missed refresh costs staleness until the next
/// session start, nothing more.
/// </summary>
static class SkillsAutoSync {
    internal static Func<ProcessStartInfo, Process?>? ProcessStarterForTesting;

    public static void SpawnDetached(string cwd) {
        try {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? "kcap") {
                WorkingDirectory       = cwd,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                // The hook's stdout is a data channel (the context envelope) — the child must
                // never inherit it. Its own output is quiet-mode small, so undrained pipes are safe.
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
            };
            psi.ArgumentList.Add("skills");
            psi.ArgumentList.Add("sync");
            psi.ArgumentList.Add("--auto");
            // The child must not inherit the hook's ambient coding-agent pipe descriptors —
            // an inherited data-channel fd would hold the agent open until the sync exits.
            ProcessHelpers.PreventInheritedHandles();
            var child = ProcessStarterForTesting is { } fake ? fake(psi) : Process.Start(psi);
            if (child is not null) {
                // Redirected pipes must not wedge the child once their buffers fill: drain both
                // to null while this process lives (the child itself also silences its streams
                // in --auto, which covers the window after this hook exits).
                child.StandardInput.Close();
                _ = child.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                _ = child.StandardError.BaseStream.CopyToAsync(Stream.Null);
            }
        } catch {
            // best effort — never break a hook
        }
    }
}
