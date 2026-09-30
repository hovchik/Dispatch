namespace Dispatch.Domain;

public sealed class RequestCollection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Collection";
    public string Description { get; set; } = string.Empty;

    /// <summary>Collection-level variables; environment variables with the same name win.</summary>
    public List<KeyValueItem> Variables { get; set; } = [];

    /// <summary>OpenAPI document (file path or URL) used by Contract assertions and the mock server.</summary>
    public string SpecLocation { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ApiRequest> Requests { get; set; } = [];
}

public sealed class ApiEnvironment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Environment";
    public List<KeyValueItem> Variables { get; set; } = [];

    /// <summary>Enabled variables as a lookup; later duplicates win, matching top-to-bottom editing intuition.</summary>
    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in Variables.Where(v => v.IsActive))
            map[v.Key.Trim()] = v.Value;
        return map;
    }

    /// <summary>Sets (or adds) a variable; returns true when something changed.</summary>
    public bool SetVariable(string name, string value)
    {
        var existing = Variables.LastOrDefault(v => v.Enabled && v.Key.Trim() == name);
        if (existing is null)
        {
            Variables.Add(new KeyValueItem(name, value));
            return true;
        }
        if (existing.Value == value)
            return false;
        existing.Value = value;
        return true;
    }
}

public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public RequestKind Kind { get; set; } = RequestKind.Http;
    public HttpVerb Method { get; set; }
    public string Url { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public double ElapsedMs { get; set; }

    /// <summary>Snapshot of the request as it was when sent (before variable resolution).</summary>
    public ApiRequest Request { get; set; } = new();

    /// <summary>Snapshot of the response (body capped) so runs can be compared later.</summary>
    public ResponseSnapshot? Response { get; set; }
}

/// <summary>A compact, persisted copy of a response for history and diffing.</summary>
public sealed class ResponseSnapshot
{
    public const int MaxBodyChars = 256 * 1024;

    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public string Body { get; set; } = string.Empty;
    public List<KeyValueItem> Headers { get; set; } = [];
    public string? Error { get; set; }

    public static ResponseSnapshot From(ApiResponse r) => new()
    {
        StatusCode = r.StatusCode,
        ReasonPhrase = r.ReasonPhrase,
        ContentType = r.ContentType,
        Body = r.Body.Length > MaxBodyChars ? r.Body[..MaxBodyChars] : r.Body,
        Headers = r.Headers.Select(h => new KeyValueItem(h.Name, h.Value)).ToList(),
        Error = r.Error
    };
}
