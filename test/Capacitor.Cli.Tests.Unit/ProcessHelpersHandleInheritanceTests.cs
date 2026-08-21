using System.Runtime.InteropServices;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the Windows half of the fix: <see cref="ProcessHelpers.PreventInheritedHandles"/> /
/// <see cref="ProcessHelpers.TryClearInheritFlag"/> stop a hook-spawned watcher from
/// inheriting the coding agent's std handles on Windows (which otherwise holds the
/// agent's hook-stdout pipe open, hanging synchronous subagent hooks and orphaning
/// the watcher). The Unix analogue — fd >= 3 CLOEXEC, a different mechanism for a
/// different leak shape — is covered separately by
/// <c>ProcessHelpersUnixFdCloexecTests</c>.
///
/// The meaningful assertion here is Windows-only — handle inheritance is a Windows
/// mechanism — so this exercises real behaviour only on Windows CI. Off Windows the
/// calls are verified to be safe no-ops.
/// </summary>
public class ProcessHelpersHandleInheritanceTests {
    const uint HANDLE_FLAG_INHERIT = 0x00000001;

    [Test]
    public async Task TryClearInheritFlag_is_a_safe_noop_on_invalid_handles() {
        // Must never throw and must reject NULL/INVALID handles rather than act on them.
        // We deliberately do NOT call PreventInheritedHandles() here: it clears the
        // inherit flag on the test host's *real* std handles and never restores them,
        // which would leave process-global state mutated for the rest of the run. The
        // actual clearing behaviour is covered in isolation by the pipe-handle test below.
        await Assert.That(ProcessHelpers.TryClearInheritFlag(0)).IsEqualTo(!OperatingSystem.IsWindows());
        await Assert.That(ProcessHelpers.TryClearInheritFlag(-1)).IsEqualTo(!OperatingSystem.IsWindows());
    }

    [Test]
    public async Task TryClearInheritFlag_removes_inherit_bit_from_an_inheritable_handle() {
        if (!OperatingSystem.IsWindows()) {
            // Defined no-op off Windows — reports success without touching the handle.
            await Assert.That(ProcessHelpers.TryClearInheritFlag(12345)).IsTrue();

            return;
        }

        var sa = new SecurityAttributes {
            nLength              = Marshal.SizeOf<SecurityAttributes>(),
            lpSecurityDescriptor = 0,
            bInheritHandle       = 1, // BOOL TRUE → both pipe ends are inheritable
        };

        await Assert.That(CreatePipe(out var read, out var write, ref sa, 0)).IsTrue();

        try {
            // Precondition: the write end really is inheritable to begin with.
            await Assert.That(GetHandleInformation(write, out var before)).IsTrue();
            await Assert.That(before & HANDLE_FLAG_INHERIT).IsEqualTo(HANDLE_FLAG_INHERIT);

            // The production primitive clears the inherit bit.
            await Assert.That(ProcessHelpers.TryClearInheritFlag(write)).IsTrue();

            // Postcondition: a process spawned now would not inherit this handle.
            await Assert.That(GetHandleInformation(write, out var after)).IsTrue();
            await Assert.That(after & HANDLE_FLAG_INHERIT).IsEqualTo(0u);
        } finally {
            CloseHandle(read);
            CloseHandle(write);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes {
        public int  nLength;
        public nint lpSecurityDescriptor;
        public int  bInheritHandle; // BOOL — int keeps the struct blittable
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreatePipe(out nint hReadPipe, out nint hWritePipe, ref SecurityAttributes lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetHandleInformation(nint hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint hObject);
}
