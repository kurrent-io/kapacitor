using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Pty.Windows;
using static Capacitor.Cli.Daemon.Pty.Windows.ConPtyInterop;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Windows;

/// <summary>
/// Windows-only raw Win32 helpers backing <see cref="ConPtyJobObjectTests"/>. Every entry
/// point throws <see cref="PlatformNotSupportedException"/> off-Windows rather than being
/// <c>#if</c>-excluded — Capacitor.Cli.Tests.Unit is one cross-platform assembly (there is no
/// WINDOWS define in this csproj), so this file must still compile — though never execute its
/// bodies — on macOS/Linux. Declared with classic <c>[DllImport]</c> rather than
/// <c>[LibraryImport]</c>: the test project isn't AOT-published, so there's no reason to also
/// opt it into <c>AllowUnsafeBlocks</c> just for these test-only calls.
/// </summary>
internal static class ConPtyJobObjectTestHelper {
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    // EntryPoint MUST be the real kernel32 export "IsProcessInJob" — the managed name
    // "IsProcessInJobNative" is a local alias (chosen so the public wrapper below can be named
    // IsProcessInJob), and without an explicit EntryPoint the marshaller looks up a
    // "IsProcessInJobNative" export that does not exist (EntryPointNotFoundException at call).
    [DllImport("kernel32.dll", EntryPoint = "IsProcessInJob", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJobNative(IntPtr processHandle, IntPtr jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// Assigns the CURRENT (test) process into <paramref name="job"/>.
    ///
    /// IRON RULE — do not break this or you will kill the CI test host:
    /// Windows has NO API to remove a process from a job, so this binds the test-runner process
    /// for its entire life. Therefore <paramref name="job"/> MUST be a job that (a) has NO
    /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE and (b) is cleaned up with CloseHandle, NEVER
    /// TerminateJobObject. A killing job would kill the host when its last handle closes; a
    /// TerminateJobObject call kills every member (the host included) unconditionally. Every
    /// call site must satisfy both conditions — audit them before adding a new one.
    /// </summary>
    internal static void AssignSelfToJob(IntPtr job) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        if (!AssignProcessToJobObject(job, GetCurrentProcess())) {
            throw new InvalidOperationException($"AssignProcessToJobObject (self) failed: {Marshal.GetLastWin32Error()}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob, int JobObjectInformationClass, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength, IntPtr lpReturnLength);

    /// <summary>
    /// Reads the <c>LimitFlags</c> of <paramref name="jobHandle"/>'s extended-limit information
    /// via the native <c>QueryInformationJobObject</c> — a READ-ONLY structural probe of a
    /// production job created by <see cref="ConPtyProcess.Spawn"/>. Callers assert the flags to
    /// prove escape is impossible by construction. This deliberately does NOT (and must never)
    /// join the test host to the killing job — see the IRON RULE on
    /// <see cref="AssignSelfToJob"/>; that self-join + dispose is exactly what killed the host.
    /// </summary>
    internal static uint QueryJobLimitFlags(IntPtr jobHandle) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        if (!QueryInformationJobObject(
                jobHandle, JobObjectExtendedLimitInformation, out var info,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), IntPtr.Zero)) {
            throw new InvalidOperationException($"QueryInformationJobObject failed: {Marshal.GetLastWin32Error()}");
        }

        return info.BasicLimitInformation.LimitFlags;
    }

    /// <summary>
    /// True iff the process identified by <paramref name="pid"/> is (transitively) a member of
    /// <paramref name="job"/>, via the native <c>IsProcessInJob</c> Win32 call.
    /// </summary>
    internal static bool IsProcessInJob(int pid, IntPtr job) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

        if (hProcess == IntPtr.Zero) {
            throw new InvalidOperationException($"OpenProcess failed: {Marshal.GetLastWin32Error()}");
        }

        try {
            if (!IsProcessInJobNative(hProcess, job, out var result)) {
                throw new InvalidOperationException($"IsProcessInJob failed: {Marshal.GetLastWin32Error()}");
            }

            return result;
        } finally {
            CloseHandle(hProcess);
        }
    }
}

/// <summary>
/// Test-only reach-through to <see cref="ConPtyProcess"/>'s internal job handle — kept as a
/// separate tiny accessor (rather than exposing the handle to all of
/// <see cref="ConPtyJobObjectTestHelper"/>'s Win32 surface) so it's obvious at the call site
/// that a test is deliberately reaching into production internals.
/// </summary>
internal static class ConPtyInteropTestAccessor {
    internal static IntPtr JobHandle(ConPtyProcess proc) => proc.JobHandleForTests;
}
