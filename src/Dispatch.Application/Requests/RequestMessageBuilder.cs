using System.Net.Http.Headers;
using System.Text;
using Dispatch.Application.Variables;
using Dispatch.Domain;

namespace Dispatch.Application.Requests;

public sealed class RequestBuildException(string message) : Exception(message);

/// <summary>Turns an <see cref="ApiRequest"/> definition into a ready-to-send <see cref="HttpRequestMessage"/>.</summary>
public interface IRequestMessageBuilder
{
    /// <exception cref="RequestBuildException">The request cannot be built (e.g. invalid URL).</exception>
    HttpRequestMessage Build(ApiRequest request, IReadOnlyDictionary<string, string> variables);
}

public sealed class RequestMessageBuilder : IRequestMessageBuilder
{
    public const string DefaultUserAgent = "Dispatch/0.1";

    // Headers that belong to HttpContent in .NET, not to HttpRequestMessage.
    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Length",
        "Content-Location", "Content-MD5", "Content-Range", "Content-Type", "Expires", "Last-Modified"
    };

    public HttpRequestMessage Build(ApiRequest request, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(request);
        string R(string s) => VariableResolver.Resolve(s, variables);

        var uri = BuildUri(request, variables);
        var message = new HttpRequestMessage(ToHttpMethod(request.Method), uri)
        {
            Content = BuildContent(request.Body, R)
        };

        foreach (var header in request.Headers.Where(h => h.IsActive))
            AddHeader(message, R(header.Key).Trim(), R(header.Value));

        ApplyAuth(message, request.Auth, R);

        if (!message.Headers.Contains("User-Agent"))
            message.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        if (!message.Headers.Contains("Accept"))
            message.Headers.TryAddWithoutValidation("Accept", "*/*");

        return message;
    }

    private static Uri BuildUri(ApiRequest request, IReadOnlyDictionary<string, string> variables)
    {
        var rawUrl = VariableResolver.Resolve(request.Url, variables).Trim();
        if (rawUrl.Length == 0)
            throw new RequestBuildException("Enter a request URL.");

        var unresolved = VariableResolver.FindUnresolved(rawUrl, variables);
        if (unresolved.Count > 0)
            throw new RequestBuildException(
                $"Unresolved variable(s) in URL: {string.Join(", ", unresolved.Select(v => $"{{{{{v}}}}}"))}. " +
                "Select an environment that defines them.");

        // The Params table is the source of truth for the query (it knows which params are disabled).
        // Fall back to the URL's own query when the table is empty (e.g. requests created in code).
        var tableParams = (request.QueryParams.Count > 0 ? request.QueryParams : QueryString.Parse(request.Url)).Where(p => p.Enabled).ToList();

        var unresolvedParams = tableParams
            .SelectMany(p => VariableResolver.FindUnresolved(p.Key, variables).Concat(VariableResolver.FindUnresolved(p.Value, variables)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unresolvedParams.Count > 0)
            throw new RequestBuildException(
                $"Unresolved variable(s) in query parameters: {string.Join(", ", unresolvedParams.Select(v => $"{{{{{v}}}}}"))}. " +
                "Select an environment that defines them.");

        // Encode what would break the query (space, #, &, +, = inside a key or value, non-ASCII) while leaving
        // already percent-encoded text alone, so pre-encoded values are not encoded twice.
        var parameters = tableParams
            .Select(p => new KeyValueItem(
                EncodeQueryComponent(VariableResolver.Resolve(p.Key, variables)),
                EncodeQueryComponent(VariableResolver.Resolve(p.Value, variables))))
            .ToList();

        if (request.Auth is { Mode: AuthMode.ApiKey, ApiKeyLocation: ApiKeyLocation.QueryParam }
            && !string.IsNullOrWhiteSpace(request.Auth.ApiKeyName))
        {
            parameters.Add(new KeyValueItem(
                Uri.EscapeDataString(VariableResolver.Resolve(request.Auth.ApiKeyName, variables)),
                Uri.EscapeDataString(VariableResolver.Resolve(request.Auth.ApiKeyValue, variables))));
        }

        var url = QueryString.WithParams(rawUrl, parameters);

        // Like Postman, default to http:// when no scheme was typed.
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new RequestBuildException($"Invalid URL: {url}");
        }

        return uri;
    }

    /// <summary>
    /// Percent-encodes a query key or value conservatively: valid <c>%XX</c> escapes and query-safe ASCII pass through
    /// untouched; everything else (space, <c>#</c>, <c>&amp;</c>, <c>+</c>, <c>=</c>, quotes, braces, non-ASCII, bare <c>%</c>)
    /// is UTF-8 percent-encoded.
    /// </summary>
    internal static string EncodeQueryComponent(string text)
    {
        if (text.All(IsQuerySafe))
            return text;

        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c == '%' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                sb.Append(text, i, 3);
                i += 3;
                continue;
            }
            if (IsQuerySafe(c))
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (!Rune.TryGetRuneAt(text, i, out var rune))
                rune = Rune.ReplacementChar;
            Span<byte> utf8 = stackalloc byte[4];
            var count = rune.EncodeToUtf8(utf8);
            foreach (var b in utf8[..count])
                sb.Append('%').Append(b.ToString("X2"));
            i += char.IsSurrogatePair(text, i) ? 2 : 1;
        }
        return sb.ToString();
    }

    // RFC 3986 unreserved + the sub-delims and pchar extras that are legal in a query, minus the delimiters that
    // would be misread by the server ('&', '=', '+', '#').
    private static bool IsQuerySafe(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '-' or '.' or '_' or '~' or '!' or '$' or '\'' or '(' or ')' or '*' or ',' or ';' or ':' or '@' or '/' or '?';

    private static HttpContent? BuildContent(RequestBody body, Func<string, string> resolve) => body.Mode switch
    {
        BodyMode.None => null,
        BodyMode.Json => new StringContent(resolve(body.Content), Encoding.UTF8, "application/json"),
        BodyMode.Text => new StringContent(resolve(body.Content), Encoding.UTF8, "text/plain"),
        BodyMode.Xml => new StringContent(resolve(body.Content), Encoding.UTF8, "application/xml"),
        BodyMode.FormUrlEncoded => new FormUrlEncodedContent(body.FormFields
            .Where(f => f.IsActive)
            .Select(f => new KeyValuePair<string, string>(resolve(f.Key), resolve(f.Value)))),
        BodyMode.Multipart => BuildMultipart(body, resolve),
        BodyMode.Binary => BuildBinary(resolve(body.FilePath)),
        _ => throw new RequestBuildException($"Unsupported body mode: {body.Mode}")
    };

    private static MultipartFormDataContent BuildMultipart(RequestBody body, Func<string, string> resolve)
    {
        var content = new MultipartFormDataContent();
        foreach (var field in body.FormFields.Where(f => f.IsActive))
        {
            var name = resolve(field.Key);
            if (field.IsFile)
            {
                var path = resolve(field.Value);
                if (!File.Exists(path))
                    throw new RequestBuildException($"File not found for form field '{name}': {path}");
                var file = new ByteArrayContent(File.ReadAllBytes(path));
                file.Headers.ContentType = new MediaTypeHeaderValue(MimeTypes.For(path));
                content.Add(file, name, Path.GetFileName(path));
            }
            else
            {
                content.Add(new StringContent(resolve(field.Value), Encoding.UTF8), name);
            }
        }
        return content;
    }

    private static ByteArrayContent BuildBinary(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new RequestBuildException("Choose a file to send as the binary body.");
        if (!File.Exists(path))
            throw new RequestBuildException($"File not found: {path}");
        var content = new ByteArrayContent(File.ReadAllBytes(path));
        content.Headers.ContentType = new MediaTypeHeaderValue(MimeTypes.For(path));
        return content;
    }

    private static void AddHeader(HttpRequestMessage message, string name, string value)
    {
        if (name.Length == 0)
            return;

        if (ContentHeaderNames.Contains(name))
        {
            // A content header without a body has nothing to attach to; ignore it like browsers do.
            if (message.Content is null)
                return;

            // User-supplied values override the defaults set by StringContent (e.g. Content-Type).
            message.Content.Headers.Remove(name);
            if (!message.Content.Headers.TryAddWithoutValidation(name, value))
                throw new RequestBuildException($"Invalid header: {name}");
            return;
        }

        if (!message.Headers.TryAddWithoutValidation(name, value))
            throw new RequestBuildException($"Invalid header: {name}");
    }

    private static void ApplyAuth(HttpRequestMessage message, AuthSettings auth, Func<string, string> resolve)
    {
        switch (auth.Mode)
        {
            case AuthMode.Bearer when !string.IsNullOrWhiteSpace(auth.Token):
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolve(auth.Token).Trim());
                break;

            case AuthMode.Basic:
                var raw = $"{resolve(auth.Username)}:{resolve(auth.Password)}";
                message.Headers.Authorization =
                    new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
                break;

            case AuthMode.ApiKey when auth.ApiKeyLocation == ApiKeyLocation.Header
                                      && !string.IsNullOrWhiteSpace(auth.ApiKeyName):
                var name = resolve(auth.ApiKeyName).Trim();
                message.Headers.Remove(name);
                if (!message.Headers.TryAddWithoutValidation(name, resolve(auth.ApiKeyValue)))
                    throw new RequestBuildException($"Invalid API key header name: {name}");
                break;
        }
    }

    public static HttpMethod ToHttpMethod(HttpVerb verb) => verb switch
    {
        HttpVerb.Get => HttpMethod.Get,
        HttpVerb.Post => HttpMethod.Post,
        HttpVerb.Put => HttpMethod.Put,
        HttpVerb.Patch => HttpMethod.Patch,
        HttpVerb.Delete => HttpMethod.Delete,
        HttpVerb.Head => HttpMethod.Head,
        HttpVerb.Options => HttpMethod.Options,
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null)
    };
}
