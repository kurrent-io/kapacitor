using Avalonia;
using ReactiveUI.Avalonia.Reactive;

namespace Capacitor.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

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
