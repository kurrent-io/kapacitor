using Avalonia;
using ReactiveUI.Avalonia.Reactive;
using Velopack;

namespace Capacitor.App;

internal static class Program
{
    /// True when Velopack relaunched this process after applying an update — set from its
    /// OnRestarted hook, which fires on a failed apply too, so it means "relaunched", not "updated".
    public static bool UpdateRelaunch { get; private set; }

    [STAThread]
    public static void Main(string[] args) {
        // Velopack's install/update hooks exit from inside Run(); anything before it would re-run
        // during those operations. Auto-apply stays off: pending packages are applied by
        // UpdateCoordinator after the install-location guard and the prerelease rule.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnRestarted(_ => UpdateRelaunch = true)
            .Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Metal stays out of the macOS renderer order: on macOS 26 it presents alternate frames with
    // different colour matching, so a focused window flickers at the caret blink rate.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions {
                RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
            })
            .UseReactiveUI(_ => { })
            .LogToTrace();
}
