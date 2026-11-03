using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dispatch.App.ViewModels.Tools;

namespace Dispatch.App.Views;

/// <summary>Hosts a tool view model (runner, mock server, import, ...) in its own window.</summary>
public partial class ToolWindow : Window
{
    public ToolWindow()
    {
        InitializeComponent();
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F1), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(ShowHelp) });
        DataContextChanged += (_, _) => HelpBar.IsVisible = (DataContext as ITool)?.HelpTopic is not null;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Tool windows are modal over the window that opened them, so the keyboard focus belongs here now.
        Activate();
        // The content is built before the window is shown and never changes on its own afterwards, so a first
        // frame lost on the way to the screen would leave the window blank until the user resizes it.
        WindowRepaint.AfterShow(this);
    }

    /// <summary>Opens a help topic; set by the dialog service.</summary>
    public Action<string?>? HelpRequested { get; set; }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => ShowHelp();

    private void ShowHelp()
    {
        if (DataContext is not HelpViewModel)
            HelpRequested?.Invoke((DataContext as ITool)?.HelpTopic);
    }
}
