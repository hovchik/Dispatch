using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Dispatch.App.ViewModels;

namespace Dispatch.App.Views;

public partial class RequestTabView : UserControl
{
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
