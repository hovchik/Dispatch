using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Auth;
using Dispatch.Application.Formatting;
using Dispatch.Application.Testing;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels;

public sealed record TimingBar(string Label, string Text, double Fraction);

public sealed partial class ResponseViewModel : ObservableObject
{
    private readonly IClipboardService _clipboard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBody), nameof(ShowRaw))]
    private bool _showPretty = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBody))]
    private string _prettyBody;

    [ObservableProperty] private BodyFormat _selectedFormat;
    [ObservableProperty] private string? _copyFeedback;

    /// <summary>JSONPath / XPath typed in the body toolbar to narrow what's shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBody), nameof(IsFiltered))]
    private string _filter = string.Empty;

    [ObservableProperty] private string? _filterError;

    private string? _filteredBody;

    private readonly IDialogService? _dialogs;

    private ResponseViewModel(ApiResponse response, BodyFormat format, string prettyBody, IClipboardService clipboard,
        IDialogService? dialogs = null, JsonTable? table = null)
    {
        _clipboard = clipboard;
        _dialogs = dialogs;
        _table = table;
        HasTable = table is not null;
        Preview = BodyPreview.Detect(response.ContentType, response.BodyBytes, response.Body);
        if (Preview == PreviewKind.Image && response.BodyBytes is { } bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                Image = new Avalonia.Media.Imaging.Bitmap(stream);
            }
            catch (Exception)
            {
                Image = null; // unsupported format (e.g. AVIF): offer to open it externally instead
            }
        }
        Model = response;
        _selectedFormat = format;
        _prettyBody = prettyBody;
        Messages = response.Messages.Select(m => new MessageItemViewModel(m)).ToList();
        Timings = BuildTimings(response);
        Jwt = DecodeJwtInBody(response.Body);
    }

    /// <summary>Decodes a JWT returned in the body (e.g. a login response's access_token), for the JWT tab.</summary>
    private static JwtInfo? DecodeJwtInBody(string body)
    {
        if (FindJwt(body) is not { } token)
            return null;
        try
        {
            return Application.Auth.Jwt.Decode(token);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static IReadOnlyList<BodyFormat> Formats { get; } = Enum.GetValues<BodyFormat>();

    /// <summary>Formats the body off the UI thread so large payloads don't freeze the window.</summary>
    public static async Task<ResponseViewModel> CreateAsync(ApiResponse response, IClipboardService clipboard, IDialogService? dialogs = null)
    {
        if (!response.HasResponse)
            return new ResponseViewModel(response, BodyFormat.Text, string.Empty, clipboard, dialogs);

        var (format, pretty, table) = await Task.Run(() =>
        {
            var detected = BodyFormatter.Detect(response.ContentType, response.Body);
            var grid = detected == BodyFormat.Json && response.BodyBytes is null ? JsonTable.Build(response.Body) : null;
            return (detected, BodyFormatter.Pretty(response.Body, detected), grid);
        });
        return new ResponseViewModel(response, format, pretty, clipboard, dialogs, table);
    }

    // ---- Table, preview, visualization and saving -----------------------------------------------

    private JsonTable? _table;

    /// <summary>JSON arrays as a sortable grid (the view rebuilds its columns when this changes).</summary>
    public JsonTable? Table
    {
        get => _table;
        private set
        {
            if (SetProperty(ref _table, value))
                OnPropertyChanged(nameof(TableSummary));
        }
    }

    /// <summary>The body holds a JSON array (the Table tab stays visible while a path is being edited).</summary>
    public bool HasTable { get; }
    public string TableSummary => TableError ?? _table?.Summary ?? "No array at that path.";

    /// <summary>Optional JSONPath choosing which array the table shows.</summary>
    [ObservableProperty] private string _tablePath = "";
    [ObservableProperty] private string? _tableError;

    partial void OnTablePathChanged(string value)
    {
        TableError = null;
        try
        {
            Table = JsonTable.Build(Model.Body, value.Trim().Length == 0 ? null : value);
        }
        catch (FormatException ex)
        {
            TableError = ex.Message;
            Table = null;
        }
        OnPropertyChanged(nameof(TableSummary));
    }

    public PreviewKind Preview { get; }
    public Avalonia.Media.Imaging.Bitmap? Image { get; }
    public bool HasImage => Image is not null;
    public bool HasPreview => Preview is not PreviewKind.None;
    public bool IsHtml => Preview == PreviewKind.Html;
    public string ImageInfo => Image is null ? "" : $"{Image.PixelSize.Width} × {Image.PixelSize.Height} px · {ContentType}";
    public string HtmlText => IsHtml ? BodyPreview.HtmlToText(Model.Body) : "";

    public string PreviewHint => Preview switch
    {
        PreviewKind.Image when Image is null => "This image format can't be shown here. Open it in your viewer.",
        PreviewKind.Svg => "SVG image: open it in your browser to see it rendered.",
        PreviewKind.Pdf => "PDF document: open it in your PDF viewer.",
        PreviewKind.Binary => $"Binary content ({ContentType ?? "unknown type"}). Save it or open it with another app.",
        PreviewKind.Html => "Text of the page below. Open it in your browser to see it rendered.",
        _ => ""
    };

    public bool HasVisualization => !string.IsNullOrEmpty(Model.Visualization);
    public string? Visualization => Model.Visualization;

    private byte[] BodyContent => Model.BodyBytes ?? System.Text.Encoding.UTF8.GetBytes(ShowPretty ? PrettyBody : Model.Body);
    private string Extension => BodyPreview.Extension(ContentType, SelectedFormat);

    [RelayCommand]
    private async Task SaveBodyAsync()
    {
        if (_dialogs is null)
            return;
        var path = await _dialogs.SaveFileAsync("Save response body", "response" + Extension);
        if (path is null)
            return;
        await File.WriteAllBytesAsync(path, BodyContent);
        CopyFeedback = "Saved";
        await Task.Delay(1500);
        CopyFeedback = null;
    }

    [RelayCommand]
    private void OpenExternally() => ShellOpener.OpenTemp("response", Extension, Model.BodyBytes ?? System.Text.Encoding.UTF8.GetBytes(Model.Body));

    [RelayCommand]
    private void OpenVisualization()
    {
        if (Model.Visualization is { } html)
            ShellOpener.OpenTemp("visualization", ".html", System.Text.Encoding.UTF8.GetBytes(html));
    }

    [RelayCommand]
    private async Task SaveTableCsvAsync()
    {
        if (_dialogs is null || Table is null)
            return;
        var path = await _dialogs.SaveFileAsync("Export table", "response.csv", new FileFilter("CSV", "*.csv"));
        if (path is not null)
            await File.WriteAllTextAsync(path, Table.ToCsv());
    }

    /// <summary>The user picked another format (e.g. the server sent JSON as text/plain): re-format the body.</summary>
    async partial void OnSelectedFormatChanged(BodyFormat value)
    {
        var pretty = await Task.Run(() => BodyFormatter.Pretty(Model.Body, value));
        if (SelectedFormat == value)
            PrettyBody = pretty;
    }

    partial void OnFilterChanged(string value)
    {
        FilterError = null;
        _filteredBody = null;
        var path = value.Trim();
        if (path.Length == 0)
            return;
        try
        {
            var isXPath = path.StartsWith('/') || SelectedFormat == BodyFormat.Xml;
            var matches = ResponseValues.Read(Model, isXPath ? ValueSource.XPath : ValueSource.JsonPath, path);
            _filteredBody = matches.Count switch
            {
                0 => "(no match)",
                1 => BodyFormatter.Pretty(matches[0], BodyFormat.Json),
                _ => BodyFormatter.Pretty("[" + string.Join(",", matches.Select(m => IsJson(m) ? m : JsonSerializer.Serialize(m))) + "]", BodyFormat.Json)
            };
        }
        catch (FormatException ex)
        {
            FilterError = ex.Message;
        }
    }

    private static bool IsJson(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[') || t is "true" or "false" or "null" || double.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    public ApiResponse Model { get; }
    public string RawBody => Model.Body;
    public string DisplayBody => IsFiltered && _filteredBody is not null ? _filteredBody : ShowPretty ? PrettyBody : RawBody;
    public bool IsFiltered => Filter.Trim().Length > 0;

    public bool ShowRaw => !ShowPretty;

    public bool HasError => !Model.HasResponse;
    public bool HasResponse => Model.HasResponse;
    public string? Error => Model.Error;
    public int StatusCode => Model.StatusCode;

    /// <summary>A code the status brush understands: HTTP codes as-is, other protocols as success / failure.</summary>
    public int StatusColorCode => Model.Kind is RequestKind.Http or RequestKind.Soap or RequestKind.GraphQl or RequestKind.Sse
        ? Model.Succeeded == false ? 500 : Model.StatusCode
        : Model.IsSuccess ? 200 : 500;

    public string StatusText => Model.Kind switch
    {
        RequestKind.Grpc => Model.ReasonPhrase,
        RequestKind.WebSocket or RequestKind.SocketIo or RequestKind.Mqtt or RequestKind.Kafka or RequestKind.Amqp
            or RequestKind.Tcp or RequestKind.Udp => Model.ReasonPhrase,
        _ => $"{Model.StatusCode} {Model.ReasonPhrase}".Trim()
    };

    public string TimeText => Format.Duration(Model.Elapsed);
    public string SizeText => Format.Bytes(Model.SizeBytes);
    public string? ContentType => Model.ContentType;
    public bool IsTruncated => Model.IsBodyTruncated;
    public string? EffectiveUrl => Model.EffectiveUrl;
    public IReadOnlyList<ResponseHeader> Headers => Model.Headers;
    public int HeaderCount => Model.Headers.Count;
    public IReadOnlyList<ResponseHeader> Trailers => Model.Trailers;
    public bool HasTrailers => Model.Trailers.Count > 0;

    public IReadOnlyList<MessageItemViewModel> Messages { get; }
    public bool HasMessages => Messages.Count > 0;
    public int MessageCount => Messages.Count(m => m.Direction == MessageDirection.Received);

    public IReadOnlyList<TestResult> Tests => Model.TestResults;
    public bool HasTests => Model.TestResults.Count > 0;
    public int PassedTests => Model.TestResults.Count(t => t.Passed);
    public string TestsSummary => $"{PassedTests}/{Model.TestResults.Count}";
    public bool AllTestsPassed => Model.AllTestsPassed;

    public IReadOnlyList<string> ScriptLog => Model.ScriptLog;
    public bool HasScriptLog => Model.ScriptLog.Count > 0;

    public string? RawRequest => Model.RawRequest;
    public bool HasRawRequest => !string.IsNullOrEmpty(Model.RawRequest);
    public IReadOnlyList<TimingBar> Timings { get; }
    public bool HasTimings => Timings.Count > 0;

    public IReadOnlyDictionary<string, string> VariableUpdates => Model.VariableUpdates;
    public bool HasVariableUpdates => Model.VariableUpdates.Count > 0;
    public string VariableUpdatesText => string.Join(Environment.NewLine, Model.VariableUpdates.Select(v => $"{v.Key} = {Truncate(v.Value)}"));

    public JwtInfo? Jwt { get; }
    public bool HasJwt => Jwt is not null;

    private static string Truncate(string s) => s.Length > 120 ? s[..117] + "…" : s;

    private static IReadOnlyList<TimingBar> BuildTimings(ApiResponse r)
    {
        if (r.Timings is not { } t)
            return [];
        var total = Math.Max(1, r.Elapsed.TotalMilliseconds);
        var bars = new List<TimingBar>();
        void Add(string label, TimeSpan? value)
        {
            if (value is { } v)
                bars.Add(new TimingBar(label, Format.Duration(v), Math.Clamp(v.TotalMilliseconds / total, 0.005, 1)));
        }
        Add("DNS lookup", t.Dns);
        Add("TCP connect", t.Connect);
        Add("TLS + waiting (TTFB)", t.FirstByte);
        Add("Content download", t.Download);
        bars.Add(new TimingBar("Total", Format.Duration(r.Elapsed), 1));
        return bars;
    }

    /// <summary>Finds a JWT-looking string value in a JSON body (e.g. access_token).</summary>
    private static string? FindJwt(string body)
    {
        if (body.Length is 0 or > 200_000 || !body.Contains("eyJ", StringComparison.Ordinal))
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(body, @"eyJ[\w-]+\.eyJ[\w-]+\.[\w-]*");
        return match.Success ? match.Value : null;
    }

    [RelayCommand]
    private void UsePretty() => ShowPretty = true;

    [RelayCommand]
    private void UseRaw() => ShowPretty = false;

    [RelayCommand]
    private void ClearFilter() => Filter = string.Empty;

    [RelayCommand]
    private async Task CopyBodyAsync()
    {
        await _clipboard.SetTextAsync(DisplayBody);
        CopyFeedback = "Copied";
        await Task.Delay(1500);
        CopyFeedback = null;
    }

    [RelayCommand]
    private Task CopyRawRequestAsync() => _clipboard.SetTextAsync(RawRequest ?? "");
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
