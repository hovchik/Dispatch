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
    /// Shows a tool (runner, mock server, ...) in its own window owned by the main window. Showing a tool that is
    /// already open brings its window to the front.
    /// </summary>
    void ShowTool(ViewModels.Tools.ITool tool);
}

/// <summary>File pickers and child windows via the main window (Avalonia's storage APIs hang off a TopLevel).</summary>
public sealed class DialogService : IDialogService
{
    private readonly Dictionary<ViewModels.Tools.ITool, Window> _open = [];
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
            tool.OnClosed();
        };
        if (_owner is null)
            window.Show();
        else
            window.Show(_owner);
    }
}
