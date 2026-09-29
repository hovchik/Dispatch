using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Formatting;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels;

public sealed partial class ResponseViewModel : ObservableObject
{
    private readonly IClipboardService _clipboard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBody))]
    private bool _showPretty = true;

    [ObservableProperty] private string? _copyFeedback;

    private ResponseViewModel(ApiResponse response, string prettyBody, IClipboardService clipboard)
    {
        _clipboard = clipboard;
        Model = response;
        PrettyBody = prettyBody;
    }

    /// <summary>Formats the body off the UI thread so large payloads don't freeze the window.</summary>
    public static async Task<ResponseViewModel> CreateAsync(ApiResponse response, IClipboardService clipboard)
    {
        var pretty = response.HasResponse
            ? await Task.Run(() => BodyFormatter.Pretty(response.Body, response.ContentType))
            : string.Empty;
        return new ResponseViewModel(response, pretty, clipboard);
    }

    public ApiResponse Model { get; }
    public string PrettyBody { get; }
    public string RawBody => Model.Body;
    public string DisplayBody => ShowPretty ? PrettyBody : RawBody;

    public bool HasError => !Model.HasResponse;
    public bool HasResponse => Model.HasResponse;
    public string? Error => Model.Error;
    public int StatusCode => Model.StatusCode;
    public string StatusText => $"{Model.StatusCode} {Model.ReasonPhrase}";
    public string TimeText => Format.Duration(Model.Elapsed);
    public string SizeText => Format.Bytes(Model.SizeBytes);
    public bool IsTruncated => Model.IsBodyTruncated;
    public string? EffectiveUrl => Model.EffectiveUrl;
    public IReadOnlyList<ResponseHeader> Headers => Model.Headers;
    public int HeaderCount => Model.Headers.Count;

    [RelayCommand]
    private async Task CopyBodyAsync()
    {
        await _clipboard.SetTextAsync(DisplayBody);
        CopyFeedback = "Copied";
        await Task.Delay(1500);
        CopyFeedback = null;
    }
}

internal static class Format
{
    public static string Duration(TimeSpan t) =>
        t.TotalMilliseconds < 1000 ? $"{t.TotalMilliseconds:0} ms" : $"{t.TotalSeconds:0.00} s";

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / (1024d * 1024d):0.##} MB"
    };

    public static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.UtcNow - when;
        return d.TotalSeconds < 60 ? "just now"
            : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m ago"
            : d.TotalHours < 24 ? $"{(int)d.TotalHours}h ago"
            : when.ToLocalTime().ToString("MMM d, HH:mm");
    }
}
