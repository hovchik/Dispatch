using Avalonia.Controls;

namespace Dispatch.App.Services;

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>Clipboard access via the main window's TopLevel (Avalonia has no global clipboard).</summary>
public sealed class ClipboardService : IClipboardService
{
    private TopLevel? _topLevel;

    public void Attach(TopLevel topLevel) => _topLevel = topLevel;

    public Task SetTextAsync(string text) =>
        _topLevel?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
}
