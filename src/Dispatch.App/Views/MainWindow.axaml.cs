using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        // Middle-click closes a tab, like in browsers. Tunnel so the press doesn't select the tab first.
        TabStrip.AddHandler(PointerPressedEvent, OnTabStripPointerPressed, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerReleasedEvent, OnTabStripPointerReleased, RoutingStrategies.Tunnel);
    }

    private RequestTabViewModel? _middlePressedTab;

    private void OnTabStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(TabStrip).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed)
            return;
        _middlePressedTab = TabAt(e.Source);
        e.Handled = _middlePressedTab is not null;
    }

    private void OnTabStripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle)
            return;
        var tab = TabAt(e.Source);
        if (tab is not null && ReferenceEquals(tab, _middlePressedTab))
        {
            tab.CloseCommand.Execute(null);
            e.Handled = true;
        }
        _middlePressedTab = null;
    }

    private static RequestTabViewModel? TabAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as RequestTabViewModel;

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
