using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Dispatch.App.ViewModels;

namespace Dispatch.App.Views;

public partial class RequestTabView : UserControl
{
    /// <summary>True shows the response next to the request editor instead of below it.</summary>
    public static readonly StyledProperty<bool> IsSideBySideProperty =
        AvaloniaProperty.Register<RequestTabView, bool>(nameof(IsSideBySide));

    private RequestTabViewModel? _viewModel;

    public RequestTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = DataContext as RequestTabViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SelectFirstVisibleTab();
        };
        ApplyLayout();
    }

    public bool IsSideBySide
    {
        get => GetValue(IsSideBySideProperty);
        set => SetValue(IsSideBySideProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsSideBySideProperty)
            ApplyLayout();
    }

    /// <summary>Moves the editor, splitter and response pane between a stacked and a side-by-side grid.</summary>
    private void ApplyLayout()
    {
        var columns = IsSideBySide;
        SplitGrid.RowDefinitions = columns ? new RowDefinitions("*") : new RowDefinitions("*,8,1.25*");
        SplitGrid.ColumnDefinitions = columns ? new ColumnDefinitions("*,10,1.15*") : new ColumnDefinitions("*");

        Grid.SetRow(EditorTabs, 0);
        Grid.SetColumn(EditorTabs, 0);
        Grid.SetRow(Splitter, columns ? 0 : 1);
        Grid.SetColumn(Splitter, columns ? 1 : 0);
        Grid.SetRow(SplitLine, columns ? 0 : 1);
        Grid.SetColumn(SplitLine, columns ? 1 : 0);
        Grid.SetRow(ResponsePane, columns ? 0 : 2);
        Grid.SetColumn(ResponsePane, columns ? 2 : 0);

        Splitter.ResizeDirection = columns ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        if (columns)
        {
            SplitLine.Width = 1;
            SplitLine.Height = double.NaN;
            SplitLine.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            SplitLine.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            ResponsePane.Margin = new Thickness(0);
        }
        else
        {
            SplitLine.Height = 1;
            SplitLine.Width = double.NaN;
            SplitLine.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            SplitLine.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            ResponsePane.Margin = new Thickness(0, 6, 0, 0);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RequestTabViewModel.Kind))
            SelectFirstVisibleTab();
    }

    /// <summary>Tabs depend on the protocol; after switching, show the first tab that applies to it.</summary>
    private void SelectFirstVisibleTab() => Dispatcher.UIThread.Post(() =>
    {
        EditorTabs.SelectedItem = EditorTabs.Items.OfType<TabItem>().FirstOrDefault(i => i.IsVisible);
    }, DispatcherPriority.Background);
}
