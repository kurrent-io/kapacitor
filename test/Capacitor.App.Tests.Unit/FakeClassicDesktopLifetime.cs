using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Capacitor.App.Tests.Unit;

/// Fake IClassicDesktopStyleApplicationLifetime for AppStartupTests' ShowStartupError coverage.
///
/// Avalonia marks the interface [NotClientImplementable]: declaring `class X :
/// IClassicDesktopStyleApplicationLifetime` directly fails to compile (CS0535 against a
/// synthesized "not implementable by user code" member an Avalonia analyzer injects).
/// DispatchProxy instead builds the implementation via runtime IL emission — it never goes
/// through the C# compiler, so that compile-time-only check never sees it — and routes every
/// interface member call into Invoke() below, where we just record what ShowStartupError does.
///
/// That indirection matters for more than just getting past the compiler: this test process
/// shares ONE live Dispatcher.UIThread across every [NotInParallel("AvaloniaSession")] test
/// (AvaloniaSession is a process-global headless session). A REAL
/// ClassicDesktopStyleApplicationLifetime's Shutdown()/TryShutdown() ends, unconditionally, in
/// Dispatcher.UIThread.InvokeShutdown() (decompiler-verified) — that would tear down the shared
/// dispatcher for every test that runs after this one. This fake never touches that machinery at
/// all: it only records the calls, so it is safe to use inside the shared session.
public class FakeClassicDesktopLifetime : DispatchProxy {
    public ShutdownMode ShutdownMode { get; private set; }
    public Window? MainWindow { get; private set; }
    public readonly List<int> ShutdownCalls = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
        switch (targetMethod?.Name) {
            case "get_ShutdownMode": return ShutdownMode;
            case "set_ShutdownMode": ShutdownMode = (ShutdownMode)args![0]!; return null;
            case "get_MainWindow": return MainWindow;
            case "set_MainWindow": MainWindow = (Window?)args![0]; return null;
            case "Shutdown": ShutdownCalls.Add(args is [int shutdownCode] ? shutdownCode : 0); return null;
            case "TryShutdown": ShutdownCalls.Add(args is [int tryShutdownCode] ? tryShutdownCode : 0); return true;
            case "get_Windows": return Array.Empty<Window>();
            case "get_Args": return Array.Empty<string>();
            case "add_ShutdownRequested":
            case "remove_ShutdownRequested":
            case "add_Startup":
            case "remove_Startup":
            case "add_Exit":
            case "remove_Exit":
                return null;
            default:
                throw new NotSupportedException($"FakeClassicDesktopLifetime: unexpected call {targetMethod?.Name}");
        }
    }

    public static (IClassicDesktopStyleApplicationLifetime Lifetime, FakeClassicDesktopLifetime Fake) Create() {
        var proxy = Create<IClassicDesktopStyleApplicationLifetime, FakeClassicDesktopLifetime>();
        return (proxy, (FakeClassicDesktopLifetime)proxy);
    }
}
