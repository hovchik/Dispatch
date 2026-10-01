using Avalonia.Controls;
using Avalonia.Data;
using Dispatch.App.ViewModels;
using Dispatch.Application.Formatting;

namespace Dispatch.App.Views;

public partial class ResponseView : UserControl
{
    private ResponseViewModel? _viewModel;

    public ResponseView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ResponseViewModel);
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
