using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Spec;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;

namespace Dispatch.Infrastructure.Interop;

/// <summary>Loads an OpenAPI / Swagger document (JSON or YAML) from a file path or URL into a <see cref="SpecDocument"/>.</summary>
public static class SpecLoader
{
    public static async Task<SpecDocument> LoadAsync(string location, IHttpClientSource? clients = null, CancellationToken ct = default)
    {
        string text;
        if (File.Exists(location))
        {
            text = await File.ReadAllTextAsync(location, ct).ConfigureAwait(false);
        }
        else if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var client = clients is null ? new HttpClient() : null;
            var http = client ?? clients!.GetClient(new RequestSettings());
            using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        else
        {
            throw new FileNotFoundException($"OpenAPI document not found: {location}");
        }
        return Parse(text, location);
    }

    public static SpecDocument Parse(string text, string? location = null)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.Length == 0)
            throw new FormatException($"{location ?? "The OpenAPI document"} is empty.");
        JsonNode? root;
        if (trimmed.StartsWith('{'))
        {
            try
            {
                root = JsonNode.Parse(trimmed, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch (JsonException ex)
            {
                throw new FormatException($"{location ?? "The OpenAPI document"} is not valid JSON: {ex.Message}");
            }
        }
        else
        {
            root = Yaml.ToJson(trimmed);
        }
        if (!SpecDocument.IsSpec(root))
            throw new FormatException($"{location ?? "The document"} is not an OpenAPI 3.x or Swagger 2.0 document.");
        return SpecDocument.Parse(root!);
    }
}
