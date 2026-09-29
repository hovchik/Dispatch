using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dispatch.App.ViewModels;

namespace Dispatch.App.Views;

public partial class CollectionsView : UserControl
{
    public CollectionsView() => InitializeComponent();

    // Single click opens a request, like Postman. (TreeView selection alone can't re-open a closed tab.)
    private void OnRequestTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: RequestNodeViewModel node })
            node.OpenCommand.Execute(null);
    }

    private void OnRenameBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
            box.PropertyChanged += (_, args) =>
            {
                if (args.Property == IsVisibleProperty && box.IsVisible)
                {
                    box.Focus();
                    box.SelectAll();
                }
            };
    }

    private void OnRenameBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CollectionNodeViewModel node } && node.IsRenaming)
            node.CommitRenameCommand.Execute(null);
    }
}
