using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Interop;
using Dispatch.Application.Testing;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;

namespace Dispatch.Infrastructure.Interop;

/// <summary>
/// Checks a response against an OpenAPI 3 / Swagger 2 document: the operation exists for the method and path, the
/// status code is documented, required response headers are present, and the body matches the documented schema.
/// </summary>
public sealed class OpenApiContractValidator(IHttpClientSource clients) : IContractValidator
{
    private readonly ConcurrentDictionary<string, (DateTime Stamp, JsonNode Spec)> _cache = new();

    public async Task<IReadOnlyList<string>> ValidateAsync(string specLocation, ApiRequest request, ApiResponse response, CancellationToken ct)
    {
        var spec = await LoadAsync(specLocation, ct).ConfigureAwait(false);
        var swagger2 = spec["swagger"] is not null;
        var method = request.Kind == RequestKind.Http ? request.Method.ToString().ToLowerInvariant() : "post";

        var url = response.EffectiveUrl ?? request.Url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return [$"Cannot determine the request path from '{url}'."];
        var requestPath = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');

        var (template, operation) = FindOperation(spec, swagger2, method, requestPath);
        if (operation is null)
            return [template is null
                ? $"No operation in the spec matches {method.ToUpperInvariant()} {requestPath}."
                : $"{template} is documented, but not for {method.ToUpperInvariant()}."];

        var responses = operation["responses"] as JsonObject ?? [];
        var code = response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var documented = responses[code] ?? responses[$"{code[0]}XX"] ?? responses[$"{code[0]}xx"] ?? responses["default"];
        if (documented is null)
            return [$"Status {code} is not documented for {method.ToUpperInvariant()} {template} (documented: {string.Join(", ", responses.Select(r => r.Key))})."];

        documented = OpenApi.Resolve(spec, documented);
        var errors = new List<string>();

        foreach (var (name, header) in documented?["headers"] as JsonObject ?? [])
        {
            var resolvedHeader = OpenApi.Resolve(spec, header);
            if (resolvedHeader?["required"]?.GetValue<bool>() == true
                && !response.Headers.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Required response header '{name}' is missing.");
        }

        JsonNode? schema;
        if (swagger2)
        {
            schema = documented?["schema"];
        }
        else
        {
            var content = documented?["content"] as JsonObject;
            if (content is null || content.Count == 0)
            {
                if (response.Body.Length > 0 && response.StatusCode != 204)
                    errors.Add("The spec documents no response body, but one was returned.");
                return errors;
            }
            var mediaType = (response.ContentType ?? "").Split(';')[0].Trim();
            var media = content[mediaType]
                        ?? content.FirstOrDefault(c => MediaMatches(c.Key, mediaType)).Value
                        ?? (mediaType.Length == 0 ? content.First().Value : null);
            if (media is null)
            {
                errors.Add($"Content-Type '{mediaType}' is not documented (documented: {string.Join(", ", content.Select(c => c.Key))}).");
                return errors;
            }
            schema = media["schema"];
        }

        if (schema is null)
            return errors;
        if (!(response.ContentType ?? "application/json").Contains("json", StringComparison.OrdinalIgnoreCase))
            return errors; // only JSON bodies are schema-validated

        JsonNode? body;
        try
        {
            body = response.Body.Length == 0 ? null : JsonNode.Parse(response.Body);
        }
        catch (JsonException ex)
        {
            errors.Add($"Response body is not valid JSON: {ex.Message}");
            return errors;
        }

        errors.AddRange(new JsonSchemaValidator(schema, spec).Validate(body));
        return errors;
    }

    private static bool MediaMatches(string pattern, string mediaType) =>
        pattern == "*/*" || (pattern.EndsWith("/*", StringComparison.Ordinal) && mediaType.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
        || (pattern.Contains("json") && mediaType.Contains("json"));

    /// <summary>Finds the operation whose path template (after the server base path) matches the request path.</summary>
    private static (string? Template, JsonObject? Operation) FindOperation(JsonNode spec, bool swagger2, string method, string requestPath)
    {
        var basePaths = swagger2
            ? [(spec["basePath"]?.ToString() ?? "").TrimEnd('/')]
            : (spec["servers"] as JsonArray ?? []).Select(s => ServerPath(s?["url"]?.ToString())).DefaultIfEmpty("").ToList();

        string? pathMatch = null;
        // Prefer literal segments over parameters: /users/me before /users/{id}.
        var paths = (spec["paths"] as JsonObject ?? [])
            .OrderBy(p => p.Key.Count(c => c == '{'))
            .ToList();
        foreach (var basePath in basePaths.Distinct())
        {
            foreach (var (template, item) in paths)
            {
                var pattern = "^" + Regex.Escape(basePath) + Regex.Replace(Regex.Escape(template.TrimEnd('/')), @"\\\{[^}]+}", "[^/]+") + "/?$";
                if (!Regex.IsMatch(requestPath, pattern, RegexOptions.IgnoreCase))
                    continue;
                pathMatch ??= template;
                if (item?[method] is JsonObject operation)
                    return (template, operation);
            }
        }
        return (pathMatch, null);
    }

    private static string ServerPath(string? serverUrl)
    {
        if (string.IsNullOrEmpty(serverUrl))
            return "";
        // Server variables: take any value for matching purposes.
        var url = Regex.Replace(serverUrl, @"\{[^}]+}", "x");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        return url.StartsWith('/') ? url.TrimEnd('/') : "";
    }

    private async Task<JsonNode> LoadAsync(string location, CancellationToken ct)
    {
        var isFile = File.Exists(location);
        var stamp = isFile ? File.GetLastWriteTimeUtc(location) : DateTime.UtcNow.Date; // URLs are re-fetched daily
        if (_cache.TryGetValue(location, out var cached) && cached.Stamp == stamp)
            return cached.Spec;

        string text;
        if (isFile)
        {
            text = await File.ReadAllTextAsync(location, ct).ConfigureAwait(false);
        }
        else if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var response = await clients.GetClient(new RequestSettings()).GetAsync(uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        else
        {
            throw new FileNotFoundException($"OpenAPI document not found: {location}");
        }

        var trimmed = text.TrimStart();
        var spec = (trimmed.StartsWith('{') ? JsonNode.Parse(trimmed) : Yaml.ToJson(trimmed))
                   ?? throw new FormatException("The OpenAPI document is empty.");
        if (!OpenApi.IsOpenApi(spec))
            throw new FormatException($"{location} is not an OpenAPI / Swagger document.");
        _cache[location] = (stamp, spec);
        return spec;
    }
}
