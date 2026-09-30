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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBody))]
    private string _prettyBody;

    [ObservableProperty] private BodyFormat _selectedFormat;
    [ObservableProperty] private string? _copyFeedback;

    private ResponseViewModel(ApiResponse response, BodyFormat format, string prettyBody, IClipboardService clipboard)
    {
        _clipboard = clipboard;
        Model = response;
        _selectedFormat = format;
        _prettyBody = prettyBody;
    }

    public static IReadOnlyList<BodyFormat> Formats { get; } = Enum.GetValues<BodyFormat>();

    /// <summary>Formats the body off the UI thread so large payloads don't freeze the window.</summary>
    public static async Task<ResponseViewModel> CreateAsync(ApiResponse response, IClipboardService clipboard)
    {
        if (!response.HasResponse)
            return new ResponseViewModel(response, BodyFormat.Text, string.Empty, clipboard);

        var (format, pretty) = await Task.Run(() =>
        {
            var detected = BodyFormatter.Detect(response.ContentType, response.Body);
            return (detected, BodyFormatter.Pretty(response.Body, detected));
        });
        return new ResponseViewModel(response, format, pretty, clipboard);
    }

    /// <summary>The user picked another format (e.g. the server sent JSON as text/plain): re-format the body.</summary>
    async partial void OnSelectedFormatChanged(BodyFormat value)
    {
        var pretty = await Task.Run(() => BodyFormatter.Pretty(Model.Body, value));
        if (SelectedFormat == value)
            PrettyBody = pretty;
    }

    public ApiResponse Model { get; }
    public string RawBody => Model.Body;
    public string DisplayBody => ShowPretty ? PrettyBody : RawBody;

    public bool HasError => !Model.HasResponse;
    public bool HasResponse => Model.HasResponse;
    public string? Error => Model.Error;
    public int StatusCode => Model.StatusCode;
    public string StatusText => $"{Model.StatusCode} {Model.ReasonPhrase}";
    public string TimeText => Format.Duration(Model.Elapsed);
    public string SizeText => Format.Bytes(Model.SizeBytes);
    public string? ContentType => Model.ContentType;
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
