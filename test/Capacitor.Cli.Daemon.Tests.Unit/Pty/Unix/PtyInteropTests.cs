using System.Reflection;
using System.Runtime.InteropServices;
using Capacitor.Cli.Daemon.Pty.Unix;
using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>
/// Library-resolution smoke tests for the PTY native interop. For each
/// <c>[LibraryImport]</c> P/Invoke we read the attribute's library name +
/// entry point at runtime, then mirror the CLR's first-call binding with
/// <see cref="NativeLibrary.Load(string, Assembly, DllImportSearchPath?)"/>
/// and <see cref="NativeLibrary.GetExport"/>. The runtime throws if either
/// the library or the symbol can't be resolved.
///
/// <para>We can't use <see cref="Marshal.Prelink"/> here because
/// <c>[LibraryImport]</c> generates the marshalling stub at compile time —
/// Prelink only binds legacy <c>[DllImport]</c> methods.</para>
///
/// <para>Reading the attribute via reflection (instead of hard-coding the
/// library name in the test) keeps the test in sync with production: if
/// someone re-introduces <c>libutil</c> on the forkpty import, this test
/// fails on glibc 2.34+ runners exactly the way the daemon did at launch.</para>
/// </summary>
public class PtyInteropTests {
    [Test]
    public Task Forkpty_PInvoke_resolves_on_unix() {
        if (OperatingSystem.IsWindows()) return Task.CompletedTask;

        AssertLibraryImportResolves(typeof(UnixPtyInterop), nameof(UnixPtyInterop.forkpty));
        return Task.CompletedTask;
    }

    [Test]
    public Task PtySetWinsizeShim_PInvoke_resolves_on_macos() {
        // The C shim (libpty_shim.dylib) is built and copied next to the
        // binary only on macOS — that's where the ARM64 variadic-ioctl ABI
        // quirk forces us to call through it. If the native build/copy step
        // ever breaks, this fires.
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;

        AssertLibraryImportResolves(typeof(UnixPtyInterop), "pty_set_winsize_shim");
        return Task.CompletedTask;
    }

    [Test]
    public Task CreatePseudoConsole_PInvoke_resolves_on_windows() {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        AssertLibraryImportResolves(typeof(ConPtyInterop), nameof(ConPtyInterop.CreatePseudoConsole));
        return Task.CompletedTask;
    }

    static void AssertLibraryImportResolves(Type type, string methodName) {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var method = type.GetMethod(methodName, flags)
                  ?? throw new InvalidOperationException(
                         $"P/Invoke method {type.Name}.{methodName} not found");

        var attr = method.GetCustomAttribute<LibraryImportAttribute>()
                ?? throw new InvalidOperationException(
                       $"{type.Name}.{methodName} is not marked [LibraryImport]");

        var entryPoint = attr.EntryPoint ?? methodName;
        var handle     = NativeLibrary.Load(attr.LibraryName, type.Assembly, searchPath: null);
        try {
            // GetExport throws EntryPointNotFoundException if the symbol is
            // missing — both branches (bad library / bad symbol) fail the test.
            NativeLibrary.GetExport(handle, entryPoint);
        } finally {
            NativeLibrary.Free(handle);
        }
    }
}
