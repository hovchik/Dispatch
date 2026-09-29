namespace Dispatch.Domain;

public sealed class RequestCollection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Collection";
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
}

public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public HttpVerb Method { get; set; }
    public string Url { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public double ElapsedMs { get; set; }

    /// <summary>Snapshot of the request as it was when sent (before variable resolution).</summary>
    public ApiRequest Request { get; set; } = new();
}
