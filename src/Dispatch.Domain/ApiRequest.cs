namespace Dispatch.Domain;

public sealed class AuthSettings
{
    public AuthMode Mode { get; set; } = AuthMode.None;
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ApiKeyName { get; set; } = "X-API-Key";
    public string ApiKeyValue { get; set; } = string.Empty;
    public ApiKeyLocation ApiKeyLocation { get; set; } = ApiKeyLocation.Header;

    public AuthSettings Clone() => (AuthSettings)MemberwiseClone();
}

public sealed class RequestBody
{
    public BodyMode Mode { get; set; } = BodyMode.None;
    public string Content { get; set; } = string.Empty;
    public List<KeyValueItem> FormFields { get; set; } = [];

    public RequestBody Clone() => new()
    {
        Mode = Mode,
        Content = Content,
        FormFields = FormFields.Select(f => f.Clone()).ToList()
    };
}

/// <summary>A saved or in-progress HTTP request definition.</summary>
public sealed class ApiRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Request";
    public HttpVerb Method { get; set; } = HttpVerb.Get;
    public string Url { get; set; } = string.Empty;
    public List<KeyValueItem> QueryParams { get; set; } = [];
    public List<KeyValueItem> Headers { get; set; } = [];
    public RequestBody Body { get; set; } = new();
    public AuthSettings Auth { get; set; } = new();

    /// <summary>Owning collection; null for unsaved scratch requests.</summary>
    public Guid? CollectionId { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Deep copy. <paramref name="newIdentity"/> assigns a fresh Id and detaches from its collection.</summary>
    public ApiRequest Clone(bool newIdentity = false) => new()
    {
        Id = newIdentity ? Guid.NewGuid() : Id,
        Name = Name,
        Method = Method,
        Url = Url,
        QueryParams = QueryParams.Select(p => p.Clone()).ToList(),
        Headers = Headers.Select(h => h.Clone()).ToList(),
        Body = Body.Clone(),
        Auth = Auth.Clone(),
        CollectionId = newIdentity ? null : CollectionId,
        SortOrder = SortOrder,
        UpdatedAt = UpdatedAt
    };
}
