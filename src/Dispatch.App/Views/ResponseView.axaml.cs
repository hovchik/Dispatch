using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Dispatch.App.ViewModels;
using Dispatch.Application.Formatting;

namespace Dispatch.App.Views;

public partial class ResponseView : UserControl
{
    /// <summary>Extra controls the host places on the status line, next to the status, time and size.</summary>
    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<ResponseView, object?>(nameof(Actions));

    /// <summary>Right margin for the tab header row, equal to the width of the status line that overlays it.</summary>
    public static readonly StyledProperty<Thickness> HeaderRightInsetProperty =
        AvaloniaProperty.Register<ResponseView, Thickness>(nameof(HeaderRightInset));

    private ResponseViewModel? _viewModel;

    public Thickness HeaderRightInset
    {
        get => GetValue(HeaderRightInsetProperty);
        set => SetValue(HeaderRightInsetProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public ResponseView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ResponseViewModel);
        StatusStrip.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty || e.Property == IsVisibleProperty)
                UpdateHeaderInset();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateHeaderInset();
        };
    }

    /// <summary>
    /// Keeps the tab headers clear of the status line. Normally the line sits at the right of the header row and the
    /// headers are inset by its width; when the pane is too narrow for both, the line gets its own row above the tabs.
    /// </summary>
    private void UpdateHeaderInset()
    {
        const double minHeaderWidth = 220;
        var stripWidth = StatusStrip.IsVisible ? StatusStrip.Bounds.Width + 8 : 0;
        var stacked = stripWidth > 0 && Bounds.Width - stripWidth < minHeaderWidth;

        var inset = stacked ? default : new Thickness(0, 0, stripWidth, 0);
        if (HeaderRightInset != inset)
            HeaderRightInset = inset;

        var row = stacked ? 1 : 0;
        if (Grid.GetRow(ResponseTabs) != row)
        {
            Grid.SetRow(ResponseTabs, row);
            Grid.SetRowSpan(ResponseTabs, stacked ? 1 : 2);
        }
    }

    private void Attach(ResponseViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = viewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelChanged;
        ShowTable(_viewModel?.Table);
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResponseViewModel.Table))
            ShowTable(_viewModel?.Table);
    }

    /// <summary>The grid's columns depend on the data, so they are built here rather than in XAML.</summary>
    private void ShowTable(JsonTable? table)
    {
        TableGrid.Columns.Clear();
        TableGrid.ItemsSource = null;
        if (table is null)
            return;
        for (var i = 0; i < table.Columns.Count; i++)
        {
            TableGrid.Columns.Add(new DataGridTextColumn
            {
                Header = table.Columns[i],
                Binding = new Binding($"[{i}]"),
                MaxWidth = 480
            });
        }
        TableGrid.ItemsSource = table.Rows;
    }
}
