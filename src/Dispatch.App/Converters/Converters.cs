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

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is string text && Enum.TryParse<HttpVerb>(text, ignoreCase: true, out var verb) ? verb : value) switch
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

/// <summary>Colors for protocol badges (non-HTTP requests show their protocol instead of a method).</summary>
public sealed class BadgeBrushConverter : IValueConverter
{
    public static readonly BadgeBrushConverter Instance = new();

    private static readonly IBrush GraphQl = MethodBrushConverter.Brush("#E535AB");
    private static readonly IBrush Grpc = MethodBrushConverter.Brush("#14B8A6");
    private static readonly IBrush Soap = MethodBrushConverter.Brush("#0EA5E9");
    private static readonly IBrush Streaming = MethodBrushConverter.Brush("#F97316");
    private static readonly IBrush Messaging = MethodBrushConverter.Brush("#8B5CF6");
    private static readonly IBrush Socket = MethodBrushConverter.Brush("#64748B");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HttpVerb => MethodBrushConverter.Instance.Convert(value, targetType, parameter, culture),
        RequestKind.Http => MethodBrushConverter.Instance.Convert(HttpVerb.Get, targetType, parameter, culture),
        RequestKind.GraphQl => GraphQl,
        RequestKind.Grpc => Grpc,
        RequestKind.Soap => Soap,
        RequestKind.WebSocket or RequestKind.Sse or RequestKind.SocketIo => Streaming,
        RequestKind.Mqtt or RequestKind.Kafka or RequestKind.Amqp => Messaging,
        _ => Socket
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MethodTextConverter : IValueConverter
{
    public static readonly MethodTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HttpVerb.Delete => "DEL",
        HttpVerb.Options => "OPT",
        HttpVerb v => v.ToString().ToUpperInvariant(),
        RequestKind.GraphQl => "GQL",
        RequestKind.Grpc => "gRPC",
        RequestKind.Soap => "SOAP",
        RequestKind.WebSocket => "WS",
        RequestKind.Sse => "SSE",
        RequestKind.SocketIo => "S.IO",
        RequestKind.Mqtt => "MQTT",
        RequestKind.Kafka => "KFK",
        RequestKind.Amqp => "AMQP",
        RequestKind.Tcp => "TCP",
        RequestKind.Udp => "UDP",
        RequestKind.Http => "HTTP",
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

    public static string Label(object value) => Instance.Convert(value, typeof(string), null, CultureInfo.InvariantCulture)?.ToString() ?? "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        BodyMode.None => "none",
        BodyMode.Json => "JSON",
        BodyMode.Text => "Text",
        BodyMode.Xml => "XML",
        BodyMode.FormUrlEncoded => "x-www-form-urlencoded",
        BodyMode.Multipart => "form-data (multipart)",
        BodyMode.Binary => "binary file",
        AuthMode.OAuth2 => "OAuth 2.0",
        AuthMode.AwsSigV4 => "AWS Signature v4",
        AuthMode.Digest => "Digest Auth",
        AuthMode.Ntlm => "NTLM / Windows",
        OAuth2GrantType.ClientCredentials => "Client credentials",
        OAuth2GrantType.Password => "Password",
        OAuth2GrantType.AuthorizationCode => "Authorization code (browser)",
        OAuth2GrantType.DeviceCode => "Device code",
        RequestKind.Http => "HTTP",
        RequestKind.GraphQl => "GraphQL",
        RequestKind.Grpc => "gRPC",
        RequestKind.Soap => "SOAP",
        RequestKind.WebSocket => "WebSocket",
        RequestKind.Sse => "Server-Sent Events",
        RequestKind.SocketIo => "Socket.IO",
        RequestKind.Mqtt => "MQTT",
        RequestKind.Kafka => "Kafka",
        RequestKind.Amqp => "AMQP (RabbitMQ)",
        RequestKind.Tcp => "TCP",
        RequestKind.Udp => "UDP",
        MessagingMode.Publish => "Publish",
        MessagingMode.Subscribe => "Subscribe",
        MessagingMode.PublishAndSubscribe => "Subscribe, then publish",
        GrpcSchemaSource.ServerReflection => "Server reflection",
        GrpcSchemaSource.ProtoFiles => ".proto files",
        SoapVersion.Soap11 => "SOAP 1.1",
        SoapVersion.Soap12 => "SOAP 1.2",
        WsSecurityMode.None => "No WS-Security",
        WsSecurityMode.UsernameTokenText => "UsernameToken (text)",
        WsSecurityMode.UsernameTokenDigest => "UsernameToken (digest)",
        HttpVersionPreference.Auto => "Auto (HTTP/1.1, upgrade when possible)",
        HttpVersionPreference.Http11 => "HTTP/1.1 only",
        HttpVersionPreference.Http2 => "HTTP/2",
        HttpVersionPreference.Http3 => "HTTP/3 (QUIC)",
        VariableScope.Environment => "Environment",
        VariableScope.Runtime => "This session",
        ValueSource.Status => "Status code",
        ValueSource.Header => "Header",
        ValueSource.JsonPath => "JSON path",
        ValueSource.XPath => "XPath",
        ValueSource.Regex => "Regex",
        ValueSource.Body => "Body text",
        ValueSource.ResponseTime => "Response time (ms)",
        ValueSource.Size => "Size (bytes)",
        ValueSource.MessageCount => "Messages received",
        ValueSource.JsonSchema => "JSON schema",
        ValueSource.Contract => "OpenAPI contract",
        AssertionOperator.Equals => "equals",
        AssertionOperator.NotEquals => "not equals",
        AssertionOperator.Contains => "contains",
        AssertionOperator.NotContains => "does not contain",
        AssertionOperator.Exists => "exists",
        AssertionOperator.NotExists => "does not exist",
        AssertionOperator.GreaterThan => ">",
        AssertionOperator.GreaterOrEqual => "≥",
        AssertionOperator.LessThan => "<",
        AssertionOperator.LessOrEqual => "≤",
        AssertionOperator.Matches => "matches regex",
        AssertionOperator.IsType => "is type",
        AssertionOperator.LengthEquals => "has length",
        AssertionOperator.IsEmpty => "is empty",
        AssertionOperator.IsNotEmpty => "is not empty",
        AssertionOperator.OneOf => "is one of",
        AssertionOperator.IsValid => "is valid",
        PayloadEncoding.Text => "Text",
        PayloadEncoding.Hex => "Hex",
        PayloadEncoding.Base64 => "Base64",
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
        Dispatch.Application.Interop.CodeTarget.Curl => "cURL",
        Dispatch.Application.Interop.CodeTarget.HttPie => "HTTPie",
        Dispatch.Application.Interop.CodeTarget.CSharp => "C# (HttpClient)",
        Dispatch.Application.Interop.CodeTarget.Python => "Python (requests)",
        Dispatch.Application.Interop.CodeTarget.JavaScript => "JavaScript (fetch)",
        Dispatch.Application.Interop.CodeTarget.Go => "Go (net/http)",
        Dispatch.Application.Interop.CodeTarget.Grpcurl => "grpcurl",
        Dispatch.Application.Interop.CodeTarget.Websocat => "websocat",
        Dispatch.Application.Diff.JsonChangeKind.TypeChanged => "Type changed",
        null => string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Label for a line ending choice ("" → None).</summary>
public sealed class LineEndingConverter : IValueConverter
{
    public static readonly LineEndingConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "" or null => "No line ending",
        "\\n" => "LF (\\n)",
        "\\r\\n" => "CRLF (\\r\\n)",
        _ => value.ToString() ?? ""
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colors a message in a stream log by direction.</summary>
public sealed class DirectionBrushConverter : IValueConverter
{
    public static readonly DirectionBrushConverter Instance = new();

    private static readonly IBrush Sent = MethodBrushConverter.Brush("#3B82F6");
    private static readonly IBrush Received = MethodBrushConverter.Brush("#22A06B");
    private static readonly IBrush Error = MethodBrushConverter.Brush("#EF4444");
    private static readonly IBrush Info = MethodBrushConverter.Brush("#8B8F98");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        MessageDirection.Sent => Sent,
        MessageDirection.Received => Received,
        MessageDirection.Error => Error,
        _ => Info
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Pass/fail colors for tests and runner results (null = not run).</summary>
public sealed class PassBrushConverter : IValueConverter
{
    public static readonly PassBrushConverter Instance = new();

    private static readonly IBrush Pass = MethodBrushConverter.Brush("#22A06B");
    private static readonly IBrush Fail = MethodBrushConverter.Brush("#EF4444");
    private static readonly IBrush None = MethodBrushConverter.Brush("#8B8F98");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => Pass,
        false => Fail,
        _ => None
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PassTextConverter : IValueConverter
{
    public static readonly PassTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => "✓",
        false => "✗",
        _ => "·"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Background tint for diff lines.</summary>
public sealed class DiffBrushConverter : IValueConverter
{
    public static readonly DiffBrushConverter Instance = new();

    private static readonly IBrush Added = MethodBrushConverter.Brush("#3322A06B");
    private static readonly IBrush Removed = MethodBrushConverter.Brush("#33EF4444");
    private static readonly IBrush Same = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Dispatch.Application.Diff.DiffKind.Added => Added,
        Dispatch.Application.Diff.DiffKind.Removed => Removed,
        _ => Same
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

/// <summary>false ↔ 0, true ↔ 1, for two-item combo boxes bound to a bool.</summary>
public sealed class BoolIndexConverter : IValueConverter
{
    public static readonly BoolIndexConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1 : 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is 1;
}
