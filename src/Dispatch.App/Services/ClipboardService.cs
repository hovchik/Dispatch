using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Dispatch.App.Services;

public interface IClipboardService
{
    Task SetTextAsync(string text);

    /// <summary>The clipboard's text, or null when it holds none. Throws when the clipboard can't be reached.</summary>
    Task<string?> GetTextAsync();
}

/// <summary>Clipboard access via the main window's TopLevel (Avalonia has no global clipboard).</summary>
public sealed class ClipboardService : IClipboardService
{
    private TopLevel? _topLevel;

    public void Attach(TopLevel topLevel) => _topLevel = topLevel;

    public Task SetTextAsync(string text) =>
        _topLevel?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    public Task<string?> GetTextAsync() =>
        _topLevel?.Clipboard is { } clipboard
            ? clipboard.TryGetTextAsync()
            : throw new InvalidOperationException("No window is attached to read the clipboard from.");
}
