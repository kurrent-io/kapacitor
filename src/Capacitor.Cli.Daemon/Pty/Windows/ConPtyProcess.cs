using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty;
using Microsoft.Win32.SafeHandles;
using static Capacitor.Cli.Daemon.Pty.Windows.ConPtyInterop;

namespace Capacitor.Cli.Daemon.Pty.Windows;

public sealed class ConPtyProcess : IPtyProcess {
    readonly IntPtr                  _hPC;
    readonly IntPtr                  _hProcess;
    readonly IntPtr                  _hOutputPipe;
    readonly FileStream              _outputStream;
    readonly FileStream              _inputStream;
    readonly SafeFileHandle          _jobHandle; // SafeHandle so a thrown exception before the ctor still gets cleaned up by the caller's catch path
    readonly CancellationTokenSource _cts = new();
    bool                             _disposed;
    int                              _pcClosed;

    public int  Pid       { get; private set; }
    public bool HasExited { get; private set; }
    public int? ExitCode  { get; private set; }

    // Test-only seam: exposes the raw job handle value so tests can QUERY the job's limit flags
    // (a read-only structural proof that breakaway is impossible). Does not transfer ownership —
    // the job is still torn down exclusively via _jobHandle.Dispose() in DisposeAsync.
    // IRON RULE: a test must NEVER AssignProcessToJobObject(this handle, self). This job carries
    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so joining the test host and then disposing this
    // process would close the last handle and have the OS kill the test host.
    internal IntPtr JobHandleForTests => _jobHandle.DangerousGetHandle();

    ConPtyProcess(IntPtr hPC, IntPtr hProcess, IntPtr hOutputPipe, FileStream outputStream, FileStream inputStream, SafeFileHandle jobHandle) {
        _hPC          = hPC;
        _hProcess     = hProcess;
        _hOutputPipe  = hOutputPipe;
        _outputStream = outputStream;
        _inputStream  = inputStream;
        _jobHandle    = jobHandle;
    }

    // Internal for unit tests: the extensionless-twin preference bug (returning npm's
    // Git-Bash "#!/bin/sh" shim instead of codex.cmd, which CreateProcessW rejects with 193)
    // is Windows-reality-specific and only reproducible by exercising this resolver directly.
    internal static (string command, bool isCmd) ResolveCommand(string command) {
        if (Path.HasExtension(command) && File.Exists(command)) {
            return (command, command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
        }

        if (Path.IsPathRooted(command)) {
            foreach (var ext in new[] { ".exe", ".cmd" }) {
                var candidate = command + ext;

                if (File.Exists(candidate)) {
                    return (candidate, ext == ".cmd");
                }
            }

            return (command, false);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            // Windows launches a bare name through PATHEXT — codex.cmd, never the
            // extensionless "#!/bin/sh" twin npm drops beside it (CreateProcessW fails 193
            // on that script). Probe the executable extensions BEFORE the bare file so a
            // .cmd/.exe always wins when both live in the same directory.
            foreach (var ext in new[] { ".exe", ".cmd" }) {
                var candidate = Path.Combine(dir, command + ext);

                if (File.Exists(candidate)) {
                    return (candidate, ext == ".cmd");
                }
            }

            var exact = Path.Combine(dir, command);

            if (File.Exists(exact)) {
                return (exact, false);
            }
        }

        return (command, false);
    }

    static IntPtr BuildEnvironmentBlock(Dictionary<string, string>? extraEnv) {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables()) {
            if (entry is { Key: string key, Value: string value }) {
                env[key] = value;
            }
        }

        env["TERM"] = "xterm-256color";

        foreach (var key in PtyEnvScrub.ClaudeSessionVars) {
            env.Remove(key);
        }

        // Clear any hosted-agent identity/routing the daemon may have inherited (e.g.
        // it was started from inside a kcap-tracked session) so the spawned agent gets
        // ONLY what extraEnv sets: hosted launches re-add these below; private local
        // launches deliberately leave them unset (no mis-tag, native permissions).
        foreach (var key in PtyEnvScrub.HostedAgentVars) {
            env.Remove(key);
        }

        // Parity with UnixPtyProcess: never leak daemon supervision state into spawned
        // children. Auto-restart is out of scope on Windows.
        foreach (var key in PtyEnvScrub.DaemonSupervisionVars) {
            env.Remove(key);
        }

        // Defense in depth (AI-1655): CaptureBootCarriers already clears these from the daemon's
        // own ambient env at boot, so this loop should normally be a no-op — but a spawned agent
        // must never see the daemon's boot-local consent-seed/expectation/attempt vars either way.
        foreach (var key in DaemonRunner.BootCarriers.All) {
            env.Remove(key);
        }

        if (extraEnv is not null) {
            foreach (var (key, value) in extraEnv) {
                env[key] = value;
            }
        }

        var sb = new StringBuilder();

        foreach (var (key, value) in env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) {
            sb.Append(key);
            sb.Append('=');
            sb.Append(value);
            sb.Append('\0');
        }

        sb.Append('\0');

        var chars     = sb.ToString().ToCharArray();
        var byteCount = chars.Length * 2;
        var block     = Marshal.AllocHGlobal(byteCount);
        Marshal.Copy(chars, 0, block, chars.Length);

        return block;
    }

    public static ConPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        ) {
        var (resolvedCommand, isCmd) = ResolveCommand(command);

        var cmdLine = new StringBuilder();

        if (isCmd) {
            cmdLine.Append("cmd.exe /c ");
        }

        cmdLine.Append(QuoteArg(resolvedCommand));

        foreach (var arg in args) {
            cmdLine.Append(' ');
            cmdLine.Append(QuoteArg(arg));
        }

        var pipeSa = new ConPtyInterop.SECURITY_ATTRIBUTES {
            nLength        = Marshal.SizeOf<ConPtyInterop.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        if (!CreatePipe(out var ptyInputRead, out var ptyInputWrite, ref pipeSa, 0)) {
            throw new InvalidOperationException($"CreatePipe (input) failed: {Marshal.GetLastWin32Error()}");
        }

        if (!CreatePipe(out var ptyOutputRead, out var ptyOutputWrite, ref pipeSa, 0)) {
            // The input pipe (both ends) is already created at this point — close it before
            // throwing so an output-pipe failure doesn't leak the two input handles.
            var err = Marshal.GetLastWin32Error();
            CloseHandle(ptyInputRead);
            CloseHandle(ptyInputWrite);
            throw new InvalidOperationException($"CreatePipe (output) failed: {err}");
        }

        var size = new ConPtyInterop.COORD { X = (short)cols, Y = (short)rows };
        var hr   = CreatePseudoConsole(size, ptyInputRead, ptyOutputWrite, 0, out var hPC);

        if (hr != 0) {
            // CreatePseudoConsole takes ownership of ptyInputRead/ptyOutputWrite ONLY on success
            // (they're closed just below in that case). On failure it owns nothing, and this throw is
            // BEFORE the committed/try-catch cleanup scope that guards the parent pipe ends — so close
            // ALL FOUR handles here or every failed CreatePseudoConsole leaks both ends of both pipes.
            CloseHandle(ptyInputRead);
            CloseHandle(ptyInputWrite);
            CloseHandle(ptyOutputRead);
            CloseHandle(ptyOutputWrite);

            throw new InvalidOperationException($"CreatePseudoConsole failed: HRESULT 0x{hr:X8}");
        }

        CloseHandle(ptyInputRead);
        CloseHandle(ptyOutputWrite);

        // OWNERSHIP / CLEANUP SCOPE: hPC (the pseudoconsole) and the two parent-side pipe ends
        // (ptyInputWrite, ptyOutputRead) belong to THIS method until the success path transfers
        // them into the returned ConPtyProcess — hPC into the object, and the pipe ends into its
        // stdio FileStreams (via SafeFileHandles). Every fail-closed exit AFTER CreatePseudoConsole
        // used to leak all three: each throw path disposed the JOB handle but never released the
        // pseudoconsole or the pipe ends. Because a CreateProcessW failure is a NORMAL fail-closed
        // outcome (bad path / blocked nesting / the fail-closed UI-restricted-job case exercised by
        // the W5 test), repeated failed launches leaked one pseudoconsole + two kernel handles each.
        // The outer catch below releases them on ANY failure; `committed` flips true only once
        // ownership has been handed to the returned object, so the success path never double-closes
        // a now-owned handle. The raw pipe locals are additionally zeroed the instant their
        // SafeFileHandles take ownership, so even a throw in the final wiring can't close a handle a
        // SafeFileHandle already owns (the outer catch skips any zeroed handle).
        var committed = false;

        try {
            // §4.1: bind the child to a KILL_ON_JOB_CLOSE job at creation time (via the
            // PROC_THREAD_ATTRIBUTE_JOB_LIST attribute below), not by AssignProcessToJobObject
            // after the fact — there is no window where the child exists uncontained.
            var hJob = CreateJobObjectW(IntPtr.Zero, null);

            if (hJob == IntPtr.Zero) {
                throw new InvalidOperationException($"CreateJobObjectW failed: {Marshal.GetLastWin32Error()}");
            }

            var limitInfo = new ConPtyInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
                BasicLimitInformation = new ConPtyInterop.JOBOBJECT_BASIC_LIMIT_INFORMATION {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            if (!SetInformationJobObject(
                    hJob, JobObjectExtendedLimitInformation, ref limitInfo,
                    (uint)Marshal.SizeOf<ConPtyInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())) {
                var err = Marshal.GetLastWin32Error();
                CloseHandle(hJob);

                throw new InvalidOperationException($"SetInformationJobObject failed: {err}");
            }

            var jobHandle = new SafeFileHandle(hJob, ownsHandle: true);

            var attrListSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attrListSize);
            var attrList = Marshal.AllocHGlobal(attrListSize);

            if (!InitializeProcThreadAttributeList(attrList, 2, 0, ref attrListSize)) {
                Marshal.FreeHGlobal(attrList);
                jobHandle.Dispose();

                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");
            }

            if (!UpdateProcThreadAttribute(
                    attrList,
                    0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hPC,
                    IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero
                )) {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
                jobHandle.Dispose();

                throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");
            }

            // PROC_THREAD_ATTRIBUTE_JOB_LIST's value is a pointer to an ARRAY of job handles — one
            // element here. The child becomes a job member at the instant CreateProcessW succeeds:
            // there is no suspended-then-assign window (AssignProcessToJobObject after the fact
            // would have one).
            var jobArray = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobArray, 0, hJob);

            if (!UpdateProcThreadAttribute(
                    attrList,
                    0,
                    PROC_THREAD_ATTRIBUTE_JOB_LIST,
                    jobArray,
                    IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero
                )) {
                var err = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(jobArray);
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
                jobHandle.Dispose();

                throw new InvalidOperationException($"UpdateProcThreadAttribute(JOB_LIST) failed: {err}");
            }

            var si = new ConPtyInterop.STARTUPINFOEXW();
            si.StartupInfo.cb      = Marshal.SizeOf<ConPtyInterop.STARTUPINFOEXW>();
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.lpAttributeList     = attrList;

            var envBlock = BuildEnvironmentBlock(extraEnv);

            try {
                const uint creationFlags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;

                if (!CreateProcessW(
                        null,
                        cmdLine.ToString(),
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        creationFlags,
                        envBlock,
                        cwd,
                        ref si,
                        out var pi
                    )) {
                    // CreateProcessW failure (bad path, access-denied, blocked nesting — all common)
                    // means no child was ever created, so the job has no members. Close its handle
                    // BEFORE throwing: the outer finally only frees the HGlobal buffers, never the
                    // job kernel handle, so without this the job would leak to GC finalization. The
                    // pseudoconsole + pipe ends are released by the outermost catch (committed == false).
                    var err = Marshal.GetLastWin32Error();
                    jobHandle.Dispose();

                    throw new InvalidOperationException($"CreateProcessW failed: {err}");
                }

                // OWNERSHIP-TRANSFER DISCIPLINE (declared outside the try so the catch can dispose
                // whatever was constructed): each raw pipe end is transferred into its SafeFileHandle
                // and its raw local zeroed in the SAME step, BEFORE the next owner is constructed — so
                // the outermost catch (which closes any still-nonzero raw local) can never double-close
                // a handle a SafeFileHandle already owns, no matter which step throws (the second wrap,
                // a FileStream ctor, or the ConPtyProcess ctor). On any failure the catch explicitly
                // disposes every owner it DID construct so a pipe end / stream is never leaked to GC on
                // this fail-closed path; a FileStream owns and closes its SafeFileHandle on Dispose, and
                // Dispose is idempotent, so disposing the stream (when present) and only otherwise the
                // bare SafeFileHandle releases each handle exactly once.
                SafeFileHandle? outputSafeHandle = null;
                SafeFileHandle? inputSafeHandle  = null;
                FileStream?     outputStream     = null;
                FileStream?     inputStream      = null;

                try {
                    CloseHandle(pi.hThread);

                    // Keep the output-read value as a plain alias for the _hOutputPipe peek/read the
                    // returned object needs; ownership still moves to outputSafeHandle below.
                    var outputPipeRaw = ptyOutputRead;

                    outputSafeHandle = new SafeFileHandle(ptyOutputRead, ownsHandle: true);
                    ptyOutputRead    = IntPtr.Zero; // ownership moved — outer catch must not close it

                    inputSafeHandle  = new SafeFileHandle(ptyInputWrite, ownsHandle: true);
                    ptyInputWrite    = IntPtr.Zero; // ownership moved — outer catch must not close it

                    outputStream = new FileStream(outputSafeHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
                    inputStream  = new FileStream(inputSafeHandle, FileAccess.Write, bufferSize: 4096, isAsync: false);

                    var process = new ConPtyProcess(hPC, pi.hProcess, outputPipeRaw, outputStream, inputStream, jobHandle) { Pid = pi.dwProcessId };
                    committed = true; // hPC + both pipe ends now owned by `process` / its streams
                    return process;
                } catch {
                    // Post-create failure: the child exists but we can't finish wiring it up. Kill
                    // it via the job (closes over descendants too) and confirm death before
                    // propagating — the caller's teardown/quarantine machinery must never see an
                    // ambiguous "maybe spawned".
                    TerminateJobObject(hJob, 1);

                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

                    while (DateTime.UtcNow < deadline) {
                        if (GetExitCodeProcess(pi.hProcess, out var code) && code != STILL_ACTIVE) break;
                        Thread.Sleep(50);
                    }

                    CloseHandle(pi.hProcess);

                    // Release each pipe-end owner exactly once: prefer the FileStream (it owns and
                    // closes its SafeFileHandle), else the bare SafeFileHandle if its stream was never
                    // constructed. Ends still held as raw non-zero locals are closed by the outer catch.
                    if (outputStream is not null) outputStream.Dispose(); else outputSafeHandle?.Dispose();
                    if (inputStream  is not null) inputStream.Dispose();  else inputSafeHandle?.Dispose();

                    jobHandle.Dispose();

                    throw;
                }
            } finally {
                Marshal.FreeHGlobal(jobArray);
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
                Marshal.FreeHGlobal(envBlock);
            }
        } catch {
            // Fail-closed release of the pseudoconsole + any parent pipe end no owner took. hPC is
            // always still ours here (transferred to the returned object only at `committed`); each
            // pipe end is zeroed the moment a SafeFileHandle owns it, so this closes each at most
            // once — never a handle now owned by the returned object or a live SafeFileHandle.
            if (!committed) {
                ClosePseudoConsole(hPC);
                if (ptyInputWrite != IntPtr.Zero) CloseHandle(ptyInputWrite);
                if (ptyOutputRead != IntPtr.Zero) CloseHandle(ptyOutputRead);
            }

            throw;
        }
    }

    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var       buf        = new byte[4096];
        var       emptyPolls = 0;

        while (!linked.Token.IsCancellationRequested) {
            if (!PeekNamedPipe(_hOutputPipe, IntPtr.Zero, 0, IntPtr.Zero, out var avail, IntPtr.Zero)) {
                CheckExited();

                yield break;
            }

            if (avail > 0) {
                emptyPolls = 0;
                var toRead = (uint)Math.Min(avail, buf.Length);

                if (!ReadFile(_hOutputPipe, buf, toRead, out var read, IntPtr.Zero) || read == 0) {
                    CheckExited();

                    yield break;
                }

                var data = new byte[read];
                Array.Copy(buf, data, (int)read);

                yield return data;
            } else {
                CheckExited();

                if (HasExited) {
                    emptyPolls++;

                    if (emptyPolls > 5) {
                        if (Interlocked.Exchange(ref _pcClosed, 1) == 0) {
                            ClosePseudoConsole(_hPC);
                        }

                        if (PeekNamedPipe(_hOutputPipe, IntPtr.Zero, 0, IntPtr.Zero, out var finalAvail, IntPtr.Zero) && finalAvail > 0) {
                            if (ReadFile(_hOutputPipe, buf, (uint)Math.Min(finalAvail, buf.Length), out var finalRead, IntPtr.Zero) && finalRead > 0) {
                                var finalData = new byte[finalRead];
                                Array.Copy(buf, finalData, (int)finalRead);

                                yield return finalData;
                            }
                        }

                        break;
                    }
                }

                try { await Task.Delay(10, linked.Token); } catch (OperationCanceledException) { yield break; }
            }
        }
    }

    public Task WriteAsync(string input) {
        var bytes = Encoding.UTF8.GetBytes(input);

        return Task.Run(() => {
                _inputStream.Write(bytes, 0, bytes.Length);
                _inputStream.Flush();
            }
        );
    }

    public Task WriteAsync(byte[] data) {
        return Task.Run(() => {
                _inputStream.Write(data, 0, data.Length);
                _inputStream.Flush();
            }
        );
    }

    public void Resize(ushort cols, ushort rows) {
        var size = new ConPtyInterop.COORD { X = (short)cols, Y = (short)rows };
        ResizePseudoConsole(_hPC, size);
    }

    public void SendInterrupt() {
        if (!HasExited) {
            try {
                _inputStream.WriteByte(0x03);
                _inputStream.Flush();
            } catch {
                /* process may have exited */
            }
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        if (HasExited) {
            return;
        }

        SendInterrupt();

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (!HasExited && DateTime.UtcNow < deadline) {
            CheckExited();
            if (!HasExited) {
                await Task.Delay(100);
            }
        }

        if (!HasExited) {
            TerminateProcess(_hProcess, 1);
            CheckExited();
        }
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        if (HasExited) {
            return;
        }

        var sw = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);

        while (!HasExited && sw.Elapsed < limit) {
            CheckExited();

            if (!HasExited) {
                await Task.Delay(50);
            }
        }
    }

    void CheckExited() {
        if (GetExitCodeProcess(_hProcess, out var exitCode) && exitCode != STILL_ACTIVE) {
            HasExited = true;
            ExitCode  = (int)exitCode;
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

        try { await _outputStream.DisposeAsync(); } catch { }

        try { await _inputStream.DisposeAsync(); } catch { }

        if (Interlocked.Exchange(ref _pcClosed, 1) == 0) {
            ClosePseudoConsole(_hPC);
        }

        CloseHandle(_hProcess);
        _jobHandle.Dispose(); // last handle to the job closes here → OS kills leader + all descendants
        _cts.Dispose();
    }

    static string QuoteArg(string arg) {
        if (arg.Length > 0 && !arg.AsSpan().ContainsAny(' ', '\t', '"')) {
            return arg;
        }

        var sb = new StringBuilder();
        sb.Append('"');

        for (var i = 0; i < arg.Length; i++) {
            var backslashes = 0;

            while (i < arg.Length && arg[i] == '\\') {
                backslashes++;
                i++;
            }

            if (i == arg.Length) {
                sb.Append('\\', backslashes * 2);

                break;
            }

            if (arg[i] == '"') {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            } else {
                sb.Append('\\', backslashes);
                sb.Append(arg[i]);
            }
        }

        sb.Append('"');

        return sb.ToString();
    }
}

public class WinPtyProcessFactory : IPtyProcessFactory {
    public IPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        )
        => ConPtyProcess.Spawn(command, args, cwd, extraEnv, cols, rows);
}
