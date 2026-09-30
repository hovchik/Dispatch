using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Dispatch.Application.Formatting;
using Dispatch.Domain;

namespace Dispatch.App.Converters;

/// <summary>Colors HTTP methods the way API developers are used to (GET green, POST amber, ...).</summary>
public sealed class MethodBrushConverter : IValueConverter
{
    public static readonly MethodBrushConverter Instance = new();

    private static readonly IBrush Get = Brush("#22A06B");
    private static readonly IBrush Post = Brush("#E3A008");
    private static readonly IBrush Put = Brush("#3B82F6");
    private static readonly IBrush Patch = Brush("#A855F7");
    private static readonly IBrush Delete = Brush("#EF4444");
    private static readonly IBrush Other = Brush("#8B8F98");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HttpVerb.Get => Get,
        HttpVerb.Post => Post,
        HttpVerb.Put => Put,
        HttpVerb.Patch => Patch,
        HttpVerb.Delete => Delete,
        _ => Other
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}

public sealed class MethodTextConverter : IValueConverter
{
    public static readonly MethodTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HttpVerb.Delete => "DEL",
        HttpVerb.Options => "OPT",
        HttpVerb v => v.ToString().ToUpperInvariant(),
        _ => string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MethodFullTextConverter : IValueConverter
{
    public static readonly MethodFullTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is HttpVerb v ? v.ToString().ToUpperInvariant() : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>2xx green, 3xx blue, 4xx amber, 5xx / transport errors red.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    /// <summary>A faint tint of the status color, for the background of the status pill.</summary>
    public static readonly StatusBrushConverter Tint = new(tint: true);

    private readonly IBrush _success, _redirect, _clientError, _serverError;

    private StatusBrushConverter(bool tint = false)
    {
        var prefix = tint ? "#2E" : "#FF";
        _success = MethodBrushConverter.Brush(prefix + "22A06B");
        _redirect = MethodBrushConverter.Brush(prefix + "3B82F6");
        _clientError = MethodBrushConverter.Brush(prefix + "E3A008");
        _serverError = MethodBrushConverter.Brush(prefix + "EF4444");
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        int and >= 200 and < 300 => _success,
        int and >= 300 and < 400 => _redirect,
        int and >= 400 and < 500 => _clientError,
        _ => _serverError
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Human-friendly labels for enums shown in combo boxes.</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public static readonly EnumLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        BodyMode.None => "none",
        BodyMode.Json => "JSON",
        BodyMode.Text => "Text",
        BodyMode.Xml => "XML",
        BodyMode.FormUrlEncoded => "x-www-form-urlencoded",
        AuthMode.None => "No Auth",
        AuthMode.Bearer => "Bearer Token",
        AuthMode.Basic => "Basic Auth",
        AuthMode.ApiKey => "API Key",
        ApiKeyLocation.Header => "Header",
        ApiKeyLocation.QueryParam => "Query Params",
        BodyFormat.Json => "JSON",
        BodyFormat.Xml => "XML",
        BodyFormat.Html => "HTML",
        BodyFormat.JavaScript => "JavaScript",
        BodyFormat.Form => "Form",
        BodyFormat.Text => "Text",
        null => string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when a count is zero; used for empty-state placeholders.</summary>
public sealed class IsZeroConverter : IValueConverter
{
    public static readonly IsZeroConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && i == 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
