using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dispatch.App.ViewModels;

namespace Dispatch.App.Views;

public partial class MainWindow : Window
{
    static MainWindow()
    {
        // Focus the tab rename box as soon as it appears (from double-click or the context menu).
        IsVisibleProperty.Changed.AddClassHandler<TextBox>((box, _) =>
        {
            if (box.IsVisible && box.Classes.Contains("tabrename"))
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.SelectAll();
                });
        });
    }

    public MainWindow() => InitializeComponent();

    private void OnTabTitleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: RequestTabViewModel tab })
        {
            tab.BeginRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTabRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RequestTabViewModel tab })
            tab.CommitRenameCommand.Execute(null);
    }
}
