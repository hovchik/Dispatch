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
        .With(Win32Options())
        .WithInterFont()
        .LogToTrace();

    /// <summary>
    /// Draw straight into the window (the classic redirection surface) instead of a Windows.UI.Composition
    /// visual tree. With the composition tree, a window that stays minimized (or behind the lock screen) for
    /// a long time comes back as an empty frame: Windows drops the composition surface and the content
    /// is never resubmitted. The app uses no Mica / acrylic effects, so nothing is lost by switching.
    /// <para>
    /// DISPATCH_RENDERING overrides this for troubleshooting: "composition" goes back to the composition tree,
    /// "software" renders on the CPU, "software-composition" does both.
    /// </para>
    /// </summary>
    private static Win32PlatformOptions Win32Options()
    {
        var mode = Environment.GetEnvironmentVariable("DISPATCH_RENDERING")?.Trim().ToLowerInvariant() ?? "";
        var composition = mode.Contains("composition");
        var software = mode.Contains("software");
        return new Win32PlatformOptions
        {
            CompositionMode = composition
                ? [Win32CompositionMode.WinUIComposition, Win32CompositionMode.RedirectionSurface]
                : [Win32CompositionMode.RedirectionSurface],
            RenderingMode = software
                ? [Win32RenderingMode.Software]
                : [Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software],
        };
    }
}
