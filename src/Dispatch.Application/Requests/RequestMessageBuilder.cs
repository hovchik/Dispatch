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
        var parameters = (request.QueryParams.Count > 0 ? request.QueryParams : QueryString.Parse(request.Url))
            .Select(p => new KeyValueItem(
                VariableResolver.Resolve(p.Key, variables),
                VariableResolver.Resolve(p.Value, variables),
                p.Enabled))
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

    private static HttpContent? BuildContent(RequestBody body, Func<string, string> resolve) => body.Mode switch
    {
        BodyMode.None => null,
        BodyMode.Json => new StringContent(resolve(body.Content), Encoding.UTF8, "application/json"),
        BodyMode.Text => new StringContent(resolve(body.Content), Encoding.UTF8, "text/plain"),
        BodyMode.Xml => new StringContent(resolve(body.Content), Encoding.UTF8, "application/xml"),
        BodyMode.FormUrlEncoded => new FormUrlEncodedContent(body.FormFields
            .Where(f => f.IsActive)
            .Select(f => new KeyValuePair<string, string>(resolve(f.Key), resolve(f.Value)))),
        _ => throw new RequestBuildException($"Unsupported body mode: {body.Mode}")
    };

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
