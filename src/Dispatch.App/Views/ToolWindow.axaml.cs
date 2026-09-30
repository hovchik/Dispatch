using Avalonia.Controls;

namespace Dispatch.App.Views;

/// <summary>Hosts a tool view model (runner, mock server, import, ...) in its own window.</summary>
public partial class ToolWindow : Window
{
    public ToolWindow() => InitializeComponent();
}
