using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace kapacitor.Daemon.Pty.Unix;

internal static partial class UnixPtyInterop {
    static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // forkpty is in libutil on both macOS and Linux

    // On macOS, forkpty takes a pointer to struct winsize. We pass IntPtr.Zero and set size after fork.
    // Alternate overload that takes WinSize ref for Linux where we can pass it directly:
    [LibraryImport("libutil", EntryPoint = "forkpty", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int forkpty(out int master, IntPtr name, IntPtr termp, ref WinSize winp);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int execvp(string file, string?[] argv);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int chdir(string path);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int setenv(string name, string value, int overwrite);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int unsetenv(string name);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint read(int fd, byte[] buf, nint count);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint write(int fd, byte[] buf, nint count);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int poll(ref PollFd fds, uint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ioctl(int fd, ulong request, ref WinSize ws);

    [LibraryImport("libc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void _exit(int status);

    // On macOS ARM64, ioctl is variadic and P/Invoke doesn't work correctly.
    // Use the native shim if available.
    [LibraryImport("libpty_shim", EntryPoint = "pty_set_winsize", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_set_winsize_shim(int fd, ushort rows, ushort cols);

    public static void SetWinSize(int fd, ushort rows, ushort cols) {
        if (IsMacOS && RuntimeInformation.OSArchitecture == Architecture.Arm64) {
            // Use C shim on macOS ARM64 (ioctl variadic ABI issue)
            pty_set_winsize_shim(fd, rows, cols);
        } else {
            // Direct ioctl on Linux x64 (works fine)
            var ws = new WinSize { ws_row = rows, ws_col = cols };

            const ulong TIOCSWINSZ = 0x5414; // Linux value
            ioctl(fd, TIOCSWINSZ, ref ws);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd {
        public int   fd;
        public short events;
        public short revents;
    }

    public const short POLLIN   = 0x01;
    public const short POLLHUP  = 0x10;
    public const short POLLERR  = 0x08;
    public const int   WNOHANG  = 1;
    public const int   SIGTERM = 15;
    public const int   SIGKILL = 9;
    public const int   SIGINT  = 2;
}
