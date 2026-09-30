using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dispatch.App.ViewModels;
using Dispatch.App.ViewModels.Tools;

namespace Dispatch.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

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

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.Palette.PropertyChanged -= OnPaletteChanged;
            _viewModel = DataContext as MainWindowViewModel;
            if (_viewModel is not null)
                _viewModel.Palette.PropertyChanged += OnPaletteChanged;
        };
    }

    private void OnPaletteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && _viewModel.Palette.IsOpen)
            Dispatcher.UIThread.Post(() => PaletteQuery.Focus(), DispatcherPriority.Background);
        else if (e.PropertyName == nameof(CommandPaletteViewModel.Selected) && _viewModel.Palette.Selected is { } selected)
            PaletteList.ScrollIntoView(selected);
    }

    // Only clicks on the dimmed backdrop itself (not inside the palette) close it.
    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender))
            _viewModel?.Palette.CloseCommand.Execute(null);
    }

    private void OnPaletteItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PaletteItem item })
            _viewModel?.Palette.RunCommand.Execute(item);
    }

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
