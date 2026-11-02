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
    }

    /// <summary>Keeps the tab headers clear of the status line, which shares their row and would otherwise cover them.</summary>
    private void UpdateHeaderInset()
    {
        var width = StatusStrip.IsVisible ? StatusStrip.Bounds.Width + 8 : 0;
        var inset = new Thickness(0, 0, width, 0);
        if (HeaderRightInset != inset)
            HeaderRightInset = inset;
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
