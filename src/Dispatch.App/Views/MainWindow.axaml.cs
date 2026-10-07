using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dispatch.App.Converters;
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
        TabStrip.AddHandler(PointerMovedEvent, OnTabStripPointerMoved, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(DragDrop.DragOverEvent, OnTabStripDragOver);
        TabStrip.AddHandler(DragDrop.DropEvent, OnTabStripDrop);
        TabStrip.LayoutUpdated += (_, _) => UpdateTabOverflow();
        TabStrip.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => UpdateTabOverflow());
    }

    /// <summary>Tabs that are scrolled out of (or clipped by) the visible part of the tab strip.</summary>
    private List<RequestTabViewModel> HiddenTabs()
    {
        var hidden = new List<RequestTabViewModel>();
        if (_viewModel is null)
            return hidden;
        var width = TabStrip.Bounds.Width;
        foreach (var tab in _viewModel.Tabs)
        {
            if (TabStrip.ContainerFromItem(tab) is not Control container ||
                container.TranslatePoint(default, TabStrip) is not { } origin)
                continue;
            if (origin.X < -0.5 || origin.X + container.Bounds.Width > width + 0.5)
                hidden.Add(tab);
        }
        return hidden;
    }

    private void UpdateTabOverflow()
    {
        var overflow = HiddenTabs().Count > 0;
        if (TabOverflowButton.IsVisible != overflow)
            TabOverflowButton.IsVisible = overflow;
    }

    private void OnTabOverflowClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var tab in HiddenTabs())
        {
            var item = new MenuItem { Header = $"{MethodTextConverter.Instance.Convert(tab.Badge, typeof(string), null, CultureInfo.CurrentCulture)}  {tab.DisplayName}" };
            item.Click += (_, _) =>
            {
                _viewModel.SelectedTab = tab;
                TabStrip.ScrollIntoView(tab);
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(TabOverflowButton);
    }

    private RequestTabViewModel? _middlePressedTab;
    private RequestTabViewModel? _dragCandidate;
    private Point _dragStart;
    private const string TabDragFormat = "dispatch/request-tab";

    private void OnTabStripPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(TabStrip);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }
        if (_dragCandidate is null)
        {
            // Remember where the press started; only tabs (not their buttons / rename box) can be dragged.
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is null &&
                v.FindAncestorOfType<TextBox>(includeSelf: true) is null)
            {
                _dragCandidate = TabAt(e.Source);
                _dragStart = point.Position;
            }
            return;
        }
        var delta = point.Position - _dragStart;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
            return;

        var tab = _dragCandidate;
        _dragCandidate = null;
        var data = new DataObject();
        data.Set(TabDragFormat, tab);
        _ = DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void OnTabStripDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(TabDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTabStripDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null || e.Data.Get(TabDragFormat) is not RequestTabViewModel tab)
            return;
        var target = TabAt(e.Source);
        if (target is not null && !ReferenceEquals(target, tab))
            _viewModel.MoveTab(tab, _viewModel.IndexOfTab(target));
        else if (target is null)
            _viewModel.MoveTab(tab, _viewModel.TabCount - 1);
        e.Handled = true;
    }

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
