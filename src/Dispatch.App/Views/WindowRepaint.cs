using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Dispatch.App.Views;

/// <summary>
/// Forces a window to draw a fresh frame. On Windows the app renders through the classic redirection surface
/// (see <c>Program.Win32Options</c>), and a frame submitted before the HWND is actually visible on screen is
/// simply dropped: the window then sits as an empty white rectangle until something dirties the visual tree
/// again, which for a static tool window is the next resize. Invalidating every visual after the window is
/// shown makes the compositor render and present a new frame while the window is on screen.
/// </summary>
internal static class WindowRepaint
{
    /// <summary>How long after showing to repaint a second time, in case the first frame raced the show.</summary>
    private static readonly TimeSpan SecondPassDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Marks every visual in the window dirty so the next render pass redraws all of it.</summary>
    public static void RepaintAll(Window window)
    {
        foreach (var visual in window.GetSelfAndVisualDescendants())
            visual.InvalidateVisual();
    }

    /// <summary>
    /// Call from <see cref="Window.OnOpened"/>. Repaints once the pending layout and render work has gone through,
    /// then once more shortly after, because the first repaint can still land before the desktop compositor
    /// has started showing the window.
    /// </summary>
    public static void AfterShow(Window window)
    {
        Dispatcher.UIThread.Post(() => RepaintAll(window), DispatcherPriority.Render);
        DispatcherTimer.RunOnce(() =>
        {
            if (window.IsVisible)
                RepaintAll(window);
        }, SecondPassDelay, DispatcherPriority.Render);
    }
}
