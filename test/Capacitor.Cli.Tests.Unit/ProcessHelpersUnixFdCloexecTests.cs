using System.Runtime.InteropServices;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the Unix half of the fix: <see cref="ProcessHelpers.PreventInheritedFileDescriptorsUnix"/>
/// marks every fd &gt;= 3 open in this process <c>FD_CLOEXEC</c> so a process spawned
/// immediately afterwards (the watcher) does not inherit it. This is a different
/// mechanism from the Windows std-handle clearing covered by
/// <see cref="ProcessHelpersHandleInheritanceTests"/>: on Unix, fds 0/1/2 are already
/// safe (exec replaces them via redirect-pipe dup2), and the leak is everything else —
/// a raw, non-cloexec <c>pipe()</c> fd is exactly the shape a hook process inherits from
/// its parent coding-agent runtime.
///
/// The meaningful assertion is Unix-only; on Windows this fd-based mechanism doesn't
/// apply (that platform has no exec-time fd table to leak through this way) so the test
/// is a no-op there.
/// </summary>
public class ProcessHelpersUnixFdCloexecTests {
    const int F_GETFD    = 1;
    const int FD_CLOEXEC = 1;

    [DllImport("libc", SetLastError = true)]
    static extern int pipe(int[] fds);

    [DllImport("libc", SetLastError = true)]
    static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    [Test]
    public async Task PreventInheritedFileDescriptorsUnix_marks_a_non_cloexec_fd_cloexec() {
        if (OperatingSystem.IsWindows()) {
            return; // Unix-only mechanism — nothing to exercise here.
        }

        var fds = new int[2];

        await Assert.That(pipe(fds)).IsEqualTo(0);

        var readFd  = fds[0];
        var writeFd = fds[1];

        try {
            // Precondition: a raw pipe() fd is NOT close-on-exec by default — this is
            // exactly the descriptor shape a hook process inherits from its parent
            // coding-agent runtime (which didn't set CLOEXEC on its own pipes), and
            // what the measured leak (fds 6,8,10,11,13 surviving into the spawned
            // watcher) was made of.
            await Assert.That(fcntl(writeFd, F_GETFD, 0) & FD_CLOEXEC).IsEqualTo(0);

            ProcessHelpers.PreventInheritedFileDescriptorsUnix();

            // Postcondition: a process spawned now would not inherit this fd.
            await Assert.That(fcntl(writeFd, F_GETFD, 0) & FD_CLOEXEC).IsEqualTo(FD_CLOEXEC);
            await Assert.That(fcntl(readFd, F_GETFD, 0) & FD_CLOEXEC).IsEqualTo(FD_CLOEXEC);
        } finally {
            _ = close(readFd);
            _ = close(writeFd);
        }
    }
}
