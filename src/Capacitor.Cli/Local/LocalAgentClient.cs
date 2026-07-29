using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Local;

/// <summary>
/// The dumb-pipe end of the local control socket: connects, sends an opening frame
/// (Spawn or Attach), puts the terminal in raw mode, and pumps bytes both ways until the
/// agent exits or the user detaches. Returns the agent's exit code (or 0 on detach).
/// </summary>
internal static class LocalAgentClient {
    public static async Task<int> RunAsync(string socketPath, LocalFrame opening, CancellationToken outerCt) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try {
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), outerCt);
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await Console.Error.WriteLineAsync($"kcap: cannot connect to daemon at {socketPath}: {ex.Message}");

            return 1;
        }

        await using var stream = new NetworkStream(sock, ownsSocket: false);
        using var       cts    = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var             ct     = cts.Token;

        await FrameCodec.WriteAsync(stream, opening, ct);

        // Touch the Console size getter once BEFORE enabling raw mode, so .NET's lazy console
        // init can't re-cook the terminal after we set raw. All real I/O bypasses Console and
        // uses the raw fds directly (TerminalRawMode.ReadStdin/WriteStdout).
        _ = TrySize();

        using var raw       = TerminalRawMode.Enable();
        var       writeLock  = new SemaphoreSlim(1, 1);
        var       exitCode   = 0;
        var       gotExited  = false; // daemon told us the agent exited
        var       errored    = false; // daemon sent an Error frame (already printed)
        var       detached   = false; // user pressed the detach sequence
        var       inputClosed = false; // local stdin reached EOF
        var       readOnly   = false; // daemon attached us to a protected agent; input is dropped

        async Task Send(LocalFrame f) {
            await writeLock.WaitAsync(ct);
            try { await FrameCodec.WriteAsync(stream, f, ct); } finally { writeLock.Release(); }
        }

        // daemon → client
        var outPump = Task.Run(async () => {
            try {
                while (!ct.IsCancellationRequested) {
                    var f = await FrameCodec.ReadAsync(stream, ct);
                    if (f is null) break;

                    switch (f.Type) {
                        case FrameType.Stdout:
                            TerminalRawMode.WriteStdout(f.Bytes, f.Bytes.Length);

                            break;
                        case FrameType.Attached:
                            var (_, snapshot) = FrameCodec.Attached(f);
                            if (snapshot.Length > 0) TerminalRawMode.WriteStdout(snapshot, snapshot.Length);

                            await Send(SizeFrame()); // nudge a clean repaint at our size

                            break;
                        case FrameType.AttachedReadOnly:
                            readOnly = true;
                            var (_, reason, roSnapshot) = FrameCodec.AttachedReadOnly(f);
                            if (roSnapshot.Length > 0) TerminalRawMode.WriteStdout(roSnapshot, roSnapshot.Length);

                            var banner = "\r\n— read-only: " + reason + ".\r\n"
                                       + "  Input is not delivered — address it with the flow tools."
                                       + " Ctrl-Q d to detach. —\r\n";
                            var bannerBytes = System.Text.Encoding.UTF8.GetBytes(banner);
                            TerminalRawMode.WriteStdout(bannerBytes, bannerBytes.Length);

                            // No SizeFrame nudge: the daemon ignores our size for a protected
                            // agent, so asking for a repaint at our dimensions would mislead.
                            break;
                        case FrameType.Exited:
                            exitCode  = f.ExitCode;
                            gotExited = true;

                            return;
                        case FrameType.Error:
                            await Console.Error.WriteLineAsync($"\r\nkcap: {f.Text}");
                            exitCode = 1;
                            errored  = true;

                            return;
                    }
                }
            } catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException) {
                /* connection closed */
            }
        }, ct);

        // SIGWINCH isn't in .NET's PosixSignal enum, so poll the window size and send a
        // Resize frame when it changes.
        var resizePump = Task.Run(async () => {
            var last = TrySize();
            try {
                while (!ct.IsCancellationRequested && !outPump.IsCompleted) {
                    await Task.Delay(300, ct);
                    var cur = TrySize();
                    if (cur != last) { last = cur; if (!readOnly) await Send(SizeFrame()); }
                }
            } catch (Exception ex) when (ex is OperationCanceledException or IOException) {
                /* shutting down */
            }
        }, ct);

        // client → daemon (raw stdin via fd 0), with detach-sequence interception. read() is
        // blocking and not cancellable; on detach we break, and on agent exit the process
        // exits (Task.WhenAny below returns), abandoning this thread — which is fine.
        var stdinPump = Task.Run(async () => {
            var scanner = new DetachScanner();
            var buf     = new byte[4096];
            try {
                while (!ct.IsCancellationRequested) {
                    var n = TerminalRawMode.ReadStdin(buf);
                    if (n <= 0) { inputClosed = true; break; }

                    var (forward, detach) = scanner.Process(buf.AsSpan(0, n));
                    if (forward.Length > 0 && !readOnly) await Send(LocalFrame.Stdin(forward));
                    if (detach) { detached = true; await Send(LocalFrame.Detach()); break; }
                }
            } catch (Exception ex) when (ex is OperationCanceledException or IOException) {
                /* shutting down */
            }
        }, ct);

        // Return as soon as the agent exits (outPump) or the user detaches/EOFs (stdinPump).
        await Task.WhenAny(outPump, stdinPump);
        await cts.CancelAsync();

        // If the connection ended without an explicit detach, stdin EOF, an Exited frame, or a
        // reported Error, the daemon dropped unexpectedly (crash / socket loss). Surface that
        // as a failure rather than letting it look like a clean exit (code 0).
        if (!detached && !inputClosed && !gotExited && !errored) {
            await Console.Error.WriteLineAsync("\r\nkcap: lost connection to the daemon");
            exitCode = 1;
        }

        // raw mode is restored by `using raw` on return.
        return exitCode;

        LocalFrame SizeFrame() { var (c, r) = TrySize(); return LocalFrame.Resize(c, r); }
    }

    static (ushort Cols, ushort Rows) TrySize() {
        try { return ((ushort)Math.Max(1, Console.WindowWidth), (ushort)Math.Max(1, Console.WindowHeight)); }
        catch { return (80, 24); }
    }
}
