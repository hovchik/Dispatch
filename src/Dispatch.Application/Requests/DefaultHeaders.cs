using Dispatch.Domain;

namespace Dispatch.Application.Requests;

/// <summary>Headers pre-filled on new requests, and the Content-Type that goes with each body mode.</summary>
public static class DefaultHeaders
{
    public const string ContentType = "Content-Type";

    /// <summary>Fresh, editable copies of the headers every new request starts with.</summary>
    public static List<KeyValueItem> Create() =>
    [
        new("Accept", "*/*"),
        new("User-Agent", RequestMessageBuilder.DefaultUserAgent),
        new("Accept-Encoding", "gzip, deflate, br"),
        new("Connection", "keep-alive"),
        new("Cache-Control", "no-cache")
    ];

    /// <summary>The Content-Type sent for <paramref name="mode"/>; null when the request has no body.</summary>
    public static string? ContentTypeFor(BodyMode mode) => mode switch
    {
        BodyMode.Json => "application/json",
        BodyMode.Text => "text/plain",
        BodyMode.Xml => "application/xml",
        BodyMode.FormUrlEncoded => "application/x-www-form-urlencoded",
        _ => null
    };

    /// <summary>True when <paramref name="value"/> was generated from a body mode (so it may be replaced automatically).</summary>
    public static bool IsGeneratedContentType(string value) =>
        value.Length == 0 || Enum.GetValues<BodyMode>().Any(m => string.Equals(ContentTypeFor(m), value.Trim(),
            StringComparison.OrdinalIgnoreCase));
}
