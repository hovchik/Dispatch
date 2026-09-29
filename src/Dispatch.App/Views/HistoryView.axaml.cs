using Avalonia.Controls;
using Avalonia.Input;
using Dispatch.App.ViewModels;

namespace Dispatch.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: HistoryItemViewModel item })
            item.OpenCommand.Execute(null);
    }
}
