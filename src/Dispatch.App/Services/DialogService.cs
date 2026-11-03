using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Dispatch.App.Services;

public sealed record FileFilter(string Name, params string[] Patterns);

public interface IDialogService
{
    Task<string?> OpenFileAsync(string title, params FileFilter[] filters);
    Task<IReadOnlyList<string>> OpenFilesAsync(string title, params FileFilter[] filters);
    Task<string?> OpenFolderAsync(string title);
    Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters);

    /// <summary>
    /// Shows a tool (runner, import, ...) in its own window. A modal tool (<see cref="ViewModels.Tools.ITool.IsModal"/>)
    /// takes focus and disables the window it was opened from (the main window, or the tool window that opened it)
    /// until it is closed, so only one chain of tool windows can be open at a time. Server tools such as the mock
    /// server stay non-modal so the request editor can talk to them. Showing a tool that is already open brings
    /// its window to the front.
    /// </summary>
    void ShowTool(ViewModels.Tools.ITool tool);
}

/// <summary>File pickers and child windows via the main window (Avalonia's storage APIs hang off a TopLevel).</summary>
public sealed class DialogService : IDialogService
{
    private readonly Dictionary<ViewModels.Tools.ITool, Window> _open = [];
    /// <summary>Open modal tool windows in the order they were opened; the last one owns the next tool window.</summary>
    private readonly List<Window> _stack = [];
    private Window? _owner;

    public void Attach(Window owner) => _owner = owner;

    /// <summary>Opens a help topic; tool windows call it from their help link and F1.</summary>
    public Action<string?>? HelpRequested { get; set; }

    private IStorageProvider Storage => _owner?.StorageProvider ?? throw new InvalidOperationException("No window to show dialogs from.");

    private static List<FilePickerFileType>? Types(FileFilter[] filters) =>
        filters.Length == 0 ? null : filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList();

    public async Task<string?> OpenFileAsync(string title, params FileFilter[] filters) =>
        (await OpenFilesInternalAsync(title, false, filters)).FirstOrDefault();

    public Task<IReadOnlyList<string>> OpenFilesAsync(string title, params FileFilter[] filters) =>
        OpenFilesInternalAsync(title, true, filters);

    private async Task<IReadOnlyList<string>> OpenFilesInternalAsync(string title, bool multiple, FileFilter[] filters)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = Types(filters)
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters)
    {
        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = Types(filters),
            ShowOverwritePrompt = true
        });
        return file?.TryGetLocalPath();
    }

    public void ShowTool(ViewModels.Tools.ITool tool)
    {
        if (_open.TryGetValue(tool, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new Views.ToolWindow
        {
            DataContext = tool, Title = $"{tool.Title} · Dispatch", Width = tool.Width, Height = tool.Height, HelpRequested = HelpRequested
        };
        _open[tool] = window;
        window.Closed += (_, _) =>
        {
            _open.Remove(tool);
            _stack.Remove(window);
            tool.OnClosed();
        };
        // A tool opened from another tool (a flow editor from the flow manager, help via F1, ...) is modal over that
        // tool's window; anything else is modal over the main window. Either way the opener is disabled until the
        // new window closes, which keeps the user from stacking up unrelated tool windows. Server tools (mock
        // server, capture proxy, ...) are the exception: they are plain owned windows, because the user sends
        // requests to them from the main window while they run.
        var owner = _stack.Count > 0 ? _stack[^1] : _owner;
        if (owner is null)
            window.Show();
        else if (!tool.IsModal)
            window.Show(owner);
        else
        {
            _stack.Add(window);
            _ = window.ShowDialog(owner);
        }
    }
}
