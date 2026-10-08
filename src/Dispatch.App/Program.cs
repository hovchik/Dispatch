using Avalonia;

namespace Dispatch.App;

internal static class Program
{
    // Avalonia configuration; don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions
        {
            // Draw straight into the window (the classic redirection surface) instead of a Windows.UI.Composition
            // visual tree. With the composition tree, a window that stays minimized (or behind the lock screen) for
            // a long time comes back as an empty frame: Windows drops the composition surface and the content
            // is never resubmitted. The app uses no Mica / acrylic effects, so nothing is lost by switching.
            CompositionMode = [Win32CompositionMode.RedirectionSurface],
        })
        .WithInterFont()
        .LogToTrace();
}
