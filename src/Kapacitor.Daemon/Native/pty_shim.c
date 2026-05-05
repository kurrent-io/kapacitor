#include <sys/ioctl.h>

// Thin wrapper around ioctl(TIOCSWINSZ) to avoid ARM64 variadic ABI issues
// when calling from .NET P/Invoke.
int pty_set_winsize(int fd, unsigned short rows, unsigned short cols) {
    struct winsize ws = {0};
    ws.ws_row = rows;
    ws.ws_col = cols;
    return ioctl(fd, TIOCSWINSZ, &ws);
}
