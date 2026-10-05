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

    /// <summary>NTLM / Negotiate domain.</summary>
    public string Domain { get; set; } = string.Empty;

    // ---- OAuth 2.0 ----
    public OAuth2GrantType OAuth2GrantType { get; set; } = OAuth2GrantType.ClientCredentials;
    public string OAuth2TokenUrl { get; set; } = string.Empty;
    public string OAuth2AuthUrl { get; set; } = string.Empty;
    public string OAuth2DeviceUrl { get; set; } = string.Empty;
    public string OAuth2ClientId { get; set; } = string.Empty;
    public string OAuth2ClientSecret { get; set; } = string.Empty;
    public string OAuth2Scope { get; set; } = string.Empty;
    public string OAuth2Audience { get; set; } = string.Empty;
    public string OAuth2RedirectUri { get; set; } = "http://127.0.0.1:53682/callback";
    public bool OAuth2UsePkce { get; set; } = true;

    /// <summary>Send client credentials in the body instead of a Basic header.</summary>
    public bool OAuth2CredentialsInBody { get; set; }

    /// <summary>Cached token state (refreshed automatically when expired).</summary>
    public string OAuth2AccessToken { get; set; } = string.Empty;
    public string OAuth2RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset? OAuth2ExpiresAt { get; set; }

    // ---- AWS Signature V4 ----
    public string AwsAccessKey { get; set; } = string.Empty;
    public string AwsSecretKey { get; set; } = string.Empty;
    public string AwsSessionToken { get; set; } = string.Empty;
    public string AwsRegion { get; set; } = "us-east-1";
    public string AwsService { get; set; } = "execute-api";

    public AuthSettings Clone() => (AuthSettings)MemberwiseClone();
}

public sealed class RequestBody
{
    public BodyMode Mode { get; set; } = BodyMode.None;
    public string Content { get; set; } = string.Empty;
    public List<KeyValueItem> FormFields { get; set; } = [];

    /// <summary>File sent as-is for <see cref="BodyMode.Binary"/>.</summary>
    public string FilePath { get; set; } = string.Empty;

    public RequestBody Clone() => new()
    {
        Mode = Mode,
        Content = Content,
        FilePath = FilePath,
        FormFields = FormFields.Select(f => f.Clone()).ToList()
    };
}

/// <summary>A saved or in-progress request definition, for any supported protocol.</summary>
public sealed class ApiRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Request";
    public RequestKind Kind { get; set; } = RequestKind.Http;
    public HttpVerb Method { get; set; } = HttpVerb.Get;

    /// <summary>Endpoint: an http(s)/ws(s) URL, gRPC host, broker address (mqtt://, kafka host list, amqp://), tcp:// / udp://.</summary>
    public string Url { get; set; } = string.Empty;
    public List<KeyValueItem> QueryParams { get; set; } = [];

    /// <summary>HTTP headers, or gRPC metadata / Kafka-style properties for other protocols.</summary>
    public List<KeyValueItem> Headers { get; set; } = [];
    public RequestBody Body { get; set; } = new();
    public AuthSettings Auth { get; set; } = new();
    public ProtocolSettings Protocol { get; set; } = new();
    public RequestSettings Settings { get; set; } = new();

    public List<Assertion> Assertions { get; set; } = [];
    public List<ExtractionRule> Extractions { get; set; } = [];

    /// <summary>Messages this request must cause on other channels (Kafka, MQTT, AMQP, WebSocket, ...).</summary>
    public List<MessageExpectation> Expectations { get; set; } = [];

    /// <summary>JavaScript run before sending (can change variables and the request).</summary>
    public string PreRequestScript { get; set; } = string.Empty;

    /// <summary>JavaScript run after the response (Postman-style <c>pm.test</c> / <c>pm.expect</c>).</summary>
    public string TestScript { get; set; } = string.Empty;

    public List<ResponseExample> Examples { get; set; } = [];
    public string Description { get; set; } = string.Empty;

    /// <summary>Owning collection; null for unsaved scratch requests.</summary>
    public Guid? CollectionId { get; set; }

    /// <summary>Folder path inside the collection, e.g. <c>Users/Admin</c>; empty = collection root.</summary>
    public string Folder { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Deep copy. <paramref name="newIdentity"/> assigns a fresh Id and detaches from its collection.</summary>
    public ApiRequest Clone(bool newIdentity = false) => new()
    {
        Id = newIdentity ? Guid.NewGuid() : Id,
        Name = Name,
        Kind = Kind,
        Method = Method,
        Url = Url,
        QueryParams = QueryParams.Select(p => p.Clone()).ToList(),
        Headers = Headers.Select(h => h.Clone()).ToList(),
        Body = Body.Clone(),
        Auth = Auth.Clone(),
        Protocol = Protocol.Clone(),
        Settings = Settings.Clone(),
        Assertions = Assertions.Select(a => a.Clone()).ToList(),
        Extractions = Extractions.Select(e => e.Clone()).ToList(),
        Expectations = Expectations.Select(e => e.Clone()).ToList(),
        PreRequestScript = PreRequestScript,
        TestScript = TestScript,
        Examples = Examples.Select(e => e.Clone()).ToList(),
        Description = Description,
        CollectionId = newIdentity ? null : CollectionId,
        Folder = Folder,
        SortOrder = SortOrder,
        UpdatedAt = UpdatedAt
    };
}
