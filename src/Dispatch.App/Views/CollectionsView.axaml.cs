using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dispatch.App.ViewModels;

namespace Dispatch.App.Views;

public partial class CollectionsView : UserControl
{
    static CollectionsView()
    {
        // Focus inline rename / move boxes as soon as they appear.
        IsVisibleProperty.Changed.AddClassHandler<TextBox>((box, _) =>
        {
            if (box.IsVisible && box.Classes.Contains("rename"))
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.SelectAll();
                });
        });
    }

    public CollectionsView() => InitializeComponent();

    // Single click opens a request, like Postman. (TreeView selection alone can't re-open a closed tab.)
    private void OnRequestTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: RequestNodeViewModel { IsMoving: false } node })
            node.OpenCommand.Execute(null);
    }

    private void OnRenameBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Control)?.DataContext)
        {
            case CollectionNodeViewModel { IsRenaming: true } collection:
                collection.CommitRenameCommand.Execute(null);
                break;
            case FolderNodeViewModel { IsRenaming: true } folder:
                folder.CommitRenameCommand.Execute(null);
                break;
            case RequestNodeViewModel { IsMoving: true } request:
                request.CommitMoveCommand.Execute(null);
                break;
        }
    }
}
