using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dispatch.Domain;

namespace Dispatch.Application.Interop;

/// <summary>
/// Dispatch's own portable formats: a single JSON file per collection (<c>*.dispatch.json</c>), or a folder with one
/// file per request so collections can live in git next to the code and be reviewed in pull requests.
/// Secret variable values are never written.
/// </summary>
public static class DispatchFormat
{
    public const string FormatVersion = "1";
    public const string CollectionFileName = "collection.dispatch.json";
    public const string RequestExtension = ".request.json";
    public const string EnvironmentExtension = ".env.json";

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Properties equal to a fresh request are pruned (see PruneDefaults) so files stay small and diffs readable.
    private static readonly ApiRequest DefaultRequest = new();

    public static bool IsDispatchJson(JsonNode root) => root["dispatch"] is not null;

    // ---- Single file -------------------------------------------------------------------------------------

    public static string ExportCollection(RequestCollection collection, IEnumerable<ApiEnvironment>? environments = null)
    {
        var root = new JsonObject
        {
            ["dispatch"] = FormatVersion,
            ["collection"] = CollectionNode(collection),
            ["requests"] = new JsonArray(collection.Requests.OrderBy(r => r.SortOrder).Select(r => (JsonNode?)RequestNode(r)).ToArray())
        };
        if (environments is not null)
            root["environments"] = new JsonArray(environments.Select(e => (JsonNode?)EnvironmentNode(e)).ToArray());
        return root.ToJsonString(Options);
    }

    public static ImportResult Import(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("Empty file.");
        if (!IsDispatchJson(root))
            throw new FormatException("Not a Dispatch collection file.");

        var result = new ImportResult { Format = "Dispatch" };
        if (root["collection"] is JsonObject collectionNode)
        {
            var collection = ReadCollection(collectionNode);
            var order = 0;
            foreach (var node in root["requests"] as JsonArray ?? [])
                collection.Requests.Add(ReadRequest(node!, collection.Id, order++));
            result.Collections.Add(collection);
        }
        else if (root["request"] is JsonObject single)
        {
            result.Collections.Add(new RequestCollection { Name = "Imported", Requests = [ReadRequest(single, null, 0)] });
        }
        foreach (var env in root["environments"] as JsonArray ?? [])
            result.Environments.Add(ReadEnvironment(env!));
        if (root["environment"] is JsonObject environment)
            result.Environments.Add(ReadEnvironment(environment));
        return result;
    }

    public static string ExportEnvironment(ApiEnvironment environment) =>
        new JsonObject { ["dispatch"] = FormatVersion, ["environment"] = EnvironmentNode(environment) }.ToJsonString(Options);

    // ---- Folder (git-friendly) ---------------------------------------------------------------------------

    /// <summary>
    /// Writes <c>collection.dispatch.json</c> plus one <c>NN-name.request.json</c> per request, in sub-folders
    /// mirroring request folders. Files of requests that no longer exist are removed.
    /// </summary>
    public static void ExportFolder(RequestCollection collection, string directory, IEnumerable<ApiEnvironment>? environments = null)
    {
        Directory.CreateDirectory(directory);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var meta = new JsonObject { ["dispatch"] = FormatVersion, ["collection"] = CollectionNode(collection) };
        var metaPath = Path.Combine(directory, CollectionFileName);
        WriteIfChanged(metaPath, meta.ToJsonString(Options));
        written.Add(Path.GetFullPath(metaPath));

        var index = 0;
        foreach (var request in collection.Requests.OrderBy(r => r.SortOrder))
        {
            var folder = Path.Combine(new[] { directory }.Concat(request.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Slug)).ToArray());
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{index++:D3}-{Slug(request.Name)}{RequestExtension}");
            var node = RequestNode(request);
            WriteIfChanged(path, node.ToJsonString(Options));
            written.Add(Path.GetFullPath(path));
        }

        foreach (var environment in environments ?? [])
        {
            var path = Path.Combine(directory, "environments", Slug(environment.Name) + EnvironmentExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteIfChanged(path, ExportEnvironment(environment));
            written.Add(Path.GetFullPath(path));
        }

        foreach (var stale in Directory.EnumerateFiles(directory, "*" + RequestExtension, SearchOption.AllDirectories))
            if (!written.Contains(Path.GetFullPath(stale)))
                File.Delete(stale);
    }

    public static ImportResult ImportFolder(string directory)
    {
        var metaPath = Path.Combine(directory, CollectionFileName);
        var collection = File.Exists(metaPath) && JsonNode.Parse(File.ReadAllText(metaPath))?["collection"] is JsonObject meta
            ? ReadCollection(meta)
            : new RequestCollection { Name = new DirectoryInfo(directory).Name };

        var files = Directory.EnumerateFiles(directory, "*" + RequestExtension, SearchOption.AllDirectories)
            .OrderBy(f => Path.GetRelativePath(directory, f), StringComparer.Ordinal)
            .ToList();
        var result = new ImportResult { Format = "Dispatch folder" };
        var order = 0;
        foreach (var file in files)
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file))!;
                var request = ReadRequest(node["request"] ?? node, collection.Id, order++);
                // The folder layout is the source of truth for folders (files may be moved around in git).
                var relativeDir = Path.GetDirectoryName(Path.GetRelativePath(directory, file))?.Replace('\\', '/') ?? "";
                if (relativeDir.Length > 0 && string.IsNullOrEmpty(request.Folder))
                    request.Folder = relativeDir;
                collection.Requests.Add(request);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                result.Warnings.Add($"{Path.GetRelativePath(directory, file)}: {ex.Message}");
            }
        }
        result.Collections.Add(collection);

        var envDir = Path.Combine(directory, "environments");
        if (Directory.Exists(envDir))
            foreach (var file in Directory.EnumerateFiles(envDir, "*" + EnvironmentExtension))
                if (JsonNode.Parse(File.ReadAllText(file))?["environment"] is JsonObject env)
                    result.Environments.Add(ReadEnvironment(env));
        return result;
    }

    private static void WriteIfChanged(string path, string content)
    {
        content = content.ReplaceLineEndings("\n") + "\n";
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return;
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        var slug = string.Join("-", sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "request" : slug.Length > 60 ? slug[..60] : slug;
    }

    // ---- Nodes -------------------------------------------------------------------------------------------

    private static JsonObject CollectionNode(RequestCollection c) => new()
    {
        ["id"] = c.Id.ToString(),
        ["name"] = c.Name,
        ["description"] = c.Description.Length > 0 ? c.Description : null,
        ["specLocation"] = c.SpecLocation.Length > 0 ? c.SpecLocation : null,
        ["variables"] = c.Variables.Count > 0 ? JsonSerializer.SerializeToNode(SafeVariables(c.Variables), Options) : null
    };

    private static JsonObject RequestNode(ApiRequest r)
    {
        var copy = r.Clone();
        copy.CollectionId = null;
        copy.SortOrder = 0;
        copy.UpdatedAt = default;
        // Cached OAuth tokens are session state, not part of the definition.
        copy.Auth.OAuth2AccessToken = "";
        copy.Auth.OAuth2RefreshToken = "";
        copy.Auth.OAuth2ExpiresAt = null;
        var node = JsonSerializer.SerializeToNode(copy, Options)!.AsObject();
        PruneDefaults(node, JsonSerializer.SerializeToNode(DefaultRequest, Options)!.AsObject());
        node["id"] = r.Id.ToString();
        node["name"] = r.Name;
        node["kind"] = r.Kind.ToString();
        return node;
    }

    /// <summary>Removes properties equal to the defaults, so a simple GET is a few lines of JSON.</summary>
    private static void PruneDefaults(JsonObject node, JsonObject defaults)
    {
        foreach (var (key, value) in node.ToList())
        {
            if (!defaults.TryGetPropertyValue(key, out var defaultValue))
                continue;
            if (value is JsonObject child && defaultValue is JsonObject childDefaults)
            {
                PruneDefaults(child, childDefaults);
                if (child.Count == 0)
                    node.Remove(key);
            }
            else if (JsonNode.DeepEquals(value, defaultValue) || value is JsonArray { Count: 0 })
            {
                node.Remove(key);
            }
        }
    }

    private static JsonObject EnvironmentNode(ApiEnvironment e) => new()
    {
        ["id"] = e.Id.ToString(),
        ["name"] = e.Name,
        ["variables"] = JsonSerializer.SerializeToNode(SafeVariables(e.Variables), Options)
    };

    private static List<KeyValueItem> SafeVariables(IEnumerable<KeyValueItem> variables) =>
        variables.Select(v => v.IsSecret ? new KeyValueItem(v.Key, "", v.Enabled) { IsSecret = true } : v.Clone()).ToList();

    private static RequestCollection ReadCollection(JsonNode node) => new()
    {
        Id = Guid.TryParse(node["id"]?.ToString(), out var id) ? id : Guid.NewGuid(),
        Name = node["name"]?.ToString() ?? "Imported",
        Description = node["description"]?.ToString() ?? "",
        SpecLocation = node["specLocation"]?.ToString() ?? "",
        Variables = node["variables"]?.Deserialize<List<KeyValueItem>>(Options) ?? []
    };

    private static ApiRequest ReadRequest(JsonNode node, Guid? collectionId, int order)
    {
        var request = node.Deserialize<ApiRequest>(Options) ?? throw new FormatException("Invalid request.");
        if (request.Id == Guid.Empty)
            request.Id = Guid.NewGuid();
        request.CollectionId = collectionId;
        request.SortOrder = order;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        return request;
    }

    private static ApiEnvironment ReadEnvironment(JsonNode node) => new()
    {
        Id = Guid.TryParse(node["id"]?.ToString(), out var id) ? id : Guid.NewGuid(),
        Name = node["name"]?.ToString() ?? "Imported",
        Variables = node["variables"]?.Deserialize<List<KeyValueItem>>(Options) ?? []
    };
}
