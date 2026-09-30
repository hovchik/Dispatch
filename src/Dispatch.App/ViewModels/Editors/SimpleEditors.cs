using CommunityToolkit.Mvvm.Input;
using Dispatch.Application.Auth;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Editors;

public sealed class AuthEditor() : ModelEditor<AuthSettings>(new AuthSettings())
{
    public static IReadOnlyList<AuthMode> Modes { get; } = Enum.GetValues<AuthMode>();
    public static IReadOnlyList<ApiKeyLocation> KeyLocations { get; } = Enum.GetValues<ApiKeyLocation>();
    public static IReadOnlyList<OAuth2GrantType> GrantTypes { get; } = Enum.GetValues<OAuth2GrantType>();

    public AuthMode Mode { get => Model.Mode; set => Set(Model.Mode, value, (m, v) => m.Mode = v); }

    public bool IsNone => Mode == AuthMode.None;
    public bool IsBearer => Mode == AuthMode.Bearer;
    public bool IsBasic => Mode is AuthMode.Basic or AuthMode.Digest or AuthMode.Ntlm;
    public bool IsNtlm => Mode == AuthMode.Ntlm;
    public bool IsApiKey => Mode == AuthMode.ApiKey;
    public bool IsOAuth2 => Mode == AuthMode.OAuth2;
    public bool IsAws => Mode == AuthMode.AwsSigV4;

    public string Token { get => Model.Token; set { if (Set(Model.Token, value, (m, v) => m.Token = v)) RefreshJwt(); } }
    public string Username { get => Model.Username; set => Set(Model.Username, value, (m, v) => m.Username = v); }
    public string Password { get => Model.Password; set => Set(Model.Password, value, (m, v) => m.Password = v); }
    public string Domain { get => Model.Domain; set => Set(Model.Domain, value, (m, v) => m.Domain = v); }
    public string ApiKeyName { get => Model.ApiKeyName; set => Set(Model.ApiKeyName, value, (m, v) => m.ApiKeyName = v); }
    public string ApiKeyValue { get => Model.ApiKeyValue; set => Set(Model.ApiKeyValue, value, (m, v) => m.ApiKeyValue = v); }
    public ApiKeyLocation ApiKeyLocation { get => Model.ApiKeyLocation; set => Set(Model.ApiKeyLocation, value, (m, v) => m.ApiKeyLocation = v); }

    public OAuth2GrantType GrantType
    {
        get => Model.OAuth2GrantType;
        set
        {
            if (Set(Model.OAuth2GrantType, value, (m, v) => m.OAuth2GrantType = v))
            {
                OnPropertyChanged(nameof(IsAuthorizationCode));
                OnPropertyChanged(nameof(IsDeviceCode));
                OnPropertyChanged(nameof(IsPasswordGrant));
            }
        }
    }

    public bool IsAuthorizationCode => GrantType == OAuth2GrantType.AuthorizationCode;
    public bool IsDeviceCode => GrantType == OAuth2GrantType.DeviceCode;
    public bool IsPasswordGrant => GrantType == OAuth2GrantType.Password;
    public string TokenUrl { get => Model.OAuth2TokenUrl; set => Set(Model.OAuth2TokenUrl, value, (m, v) => m.OAuth2TokenUrl = v); }
    public string AuthUrl { get => Model.OAuth2AuthUrl; set => Set(Model.OAuth2AuthUrl, value, (m, v) => m.OAuth2AuthUrl = v); }
    public string DeviceUrl { get => Model.OAuth2DeviceUrl; set => Set(Model.OAuth2DeviceUrl, value, (m, v) => m.OAuth2DeviceUrl = v); }
    public string ClientId { get => Model.OAuth2ClientId; set => Set(Model.OAuth2ClientId, value, (m, v) => m.OAuth2ClientId = v); }
    public string ClientSecret { get => Model.OAuth2ClientSecret; set => Set(Model.OAuth2ClientSecret, value, (m, v) => m.OAuth2ClientSecret = v); }
    public string Scope { get => Model.OAuth2Scope; set => Set(Model.OAuth2Scope, value, (m, v) => m.OAuth2Scope = v); }
    public string Audience { get => Model.OAuth2Audience; set => Set(Model.OAuth2Audience, value, (m, v) => m.OAuth2Audience = v); }
    public string RedirectUri { get => Model.OAuth2RedirectUri; set => Set(Model.OAuth2RedirectUri, value, (m, v) => m.OAuth2RedirectUri = v); }
    public bool UsePkce { get => Model.OAuth2UsePkce; set => Set(Model.OAuth2UsePkce, value, (m, v) => m.OAuth2UsePkce = v); }
    public bool CredentialsInBody { get => Model.OAuth2CredentialsInBody; set => Set(Model.OAuth2CredentialsInBody, value, (m, v) => m.OAuth2CredentialsInBody = v); }

    public string CachedTokenSummary => string.IsNullOrEmpty(Model.OAuth2AccessToken)
        ? "No token yet: one is fetched on the next send."
        : Model.OAuth2ExpiresAt is { } exp
            ? exp > DateTimeOffset.UtcNow ? $"Cached token valid until {exp.ToLocalTime():g}" : "Cached token expired; it will be refreshed."
            : "Cached token (no expiry given).";

    public string AwsAccessKey { get => Model.AwsAccessKey; set => Set(Model.AwsAccessKey, value, (m, v) => m.AwsAccessKey = v); }
    public string AwsSecretKey { get => Model.AwsSecretKey; set => Set(Model.AwsSecretKey, value, (m, v) => m.AwsSecretKey = v); }
    public string AwsSessionToken { get => Model.AwsSessionToken; set => Set(Model.AwsSessionToken, value, (m, v) => m.AwsSessionToken = v); }
    public string AwsRegion { get => Model.AwsRegion; set => Set(Model.AwsRegion, value, (m, v) => m.AwsRegion = v); }
    public string AwsService { get => Model.AwsService; set => Set(Model.AwsService, value, (m, v) => m.AwsService = v); }

    // ---- JWT inspection of the bearer token ----

    public JwtInfo? Jwt { get; private set; }
    public bool HasJwt => Jwt is not null;
    public string JwtSummary => Jwt?.Summary(DateTimeOffset.UtcNow) ?? "";

    private void RefreshJwt()
    {
        try
        {
            Jwt = Application.Auth.Jwt.LooksLikeJwt(Token) ? Application.Auth.Jwt.Decode(Token) : null;
        }
        catch (FormatException)
        {
            Jwt = null;
        }
        OnPropertyChanged(nameof(Jwt));
        OnPropertyChanged(nameof(HasJwt));
        OnPropertyChanged(nameof(JwtSummary));
    }

    protected override void OnLoaded() => RefreshJwt();

    /// <summary>Cache a token the sender fetched, without marking the request dirty.</summary>
    public void CacheToken(AuthSettings refreshed)
    {
        Model.OAuth2AccessToken = refreshed.OAuth2AccessToken;
        Model.OAuth2RefreshToken = refreshed.OAuth2RefreshToken;
        Model.OAuth2ExpiresAt = refreshed.OAuth2ExpiresAt;
        base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(CachedTokenSummary)));
    }

    private RelayCommand? _clearTokenCommand;

    public IRelayCommand ClearTokenCommand => _clearTokenCommand ??= new RelayCommand(() =>
    {
        Model.OAuth2AccessToken = "";
        Model.OAuth2RefreshToken = "";
        Model.OAuth2ExpiresAt = null;
        OnPropertyChanged(nameof(CachedTokenSummary));
    });

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Mode) or "")
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsNone)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsBearer)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsBasic)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsNtlm)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsApiKey)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsOAuth2)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsAws)));
        }
    }
}

public sealed class SettingsEditor() : ModelEditor<RequestSettings>(new RequestSettings())
{
    public static IReadOnlyList<HttpVersionPreference> HttpVersions { get; } = Enum.GetValues<HttpVersionPreference>();

    public decimal? TimeoutMs { get => Model.TimeoutMs == 0 ? null : Model.TimeoutMs; set => Set(Model.TimeoutMs, (int)(value ?? 0), (m, v) => m.TimeoutMs = v); }
    public bool FollowRedirects { get => Model.FollowRedirects; set => Set(Model.FollowRedirects, value, (m, v) => m.FollowRedirects = v); }
    public decimal MaxRedirects { get => Model.MaxRedirects; set => Set(Model.MaxRedirects, (int)value, (m, v) => m.MaxRedirects = v); }
    public bool VerifySsl { get => Model.VerifySsl; set => Set(Model.VerifySsl, value, (m, v) => m.VerifySsl = v); }
    public HttpVersionPreference HttpVersion { get => Model.HttpVersion; set => Set(Model.HttpVersion, value, (m, v) => m.HttpVersion = v); }
    public string Proxy { get => Model.Proxy; set => Set(Model.Proxy, value, (m, v) => m.Proxy = v); }
    public bool UseSystemProxy { get => Model.UseSystemProxy; set => Set(Model.UseSystemProxy, value, (m, v) => m.UseSystemProxy = v); }
    public string ClientCertificatePath { get => Model.ClientCertificatePath; set => Set(Model.ClientCertificatePath, value, (m, v) => m.ClientCertificatePath = v); }
    public string ClientCertificateKeyPath { get => Model.ClientCertificateKeyPath; set => Set(Model.ClientCertificateKeyPath, value, (m, v) => m.ClientCertificateKeyPath = v); }
    public string ClientCertificatePassword { get => Model.ClientCertificatePassword; set => Set(Model.ClientCertificatePassword, value, (m, v) => m.ClientCertificatePassword = v); }
    public bool UseCookieJar { get => Model.UseCookieJar; set => Set(Model.UseCookieJar, value, (m, v) => m.UseCookieJar = v); }
}

/// <summary>WebSocket and Server-Sent Events.</summary>
public sealed class StreamEditor : ModelEditor<StreamSettings>
{
    public StreamEditor() : base(new StreamSettings())
    {
        InitialMessages = new KeyValueListViewModel("Label", "Message sent on connect");
        SavedMessages = new KeyValueListViewModel("Label", "Message");
        InitialMessages.Changed += (_, _) => { if (!IsLoading) { Model.InitialMessages = InitialMessages.ToItems(); RaiseChanged(); } };
        SavedMessages.Changed += (_, _) => { if (!IsLoading) { Model.SavedMessages = SavedMessages.ToItems(); RaiseChanged(); } };
    }

    public KeyValueListViewModel InitialMessages { get; }
    public KeyValueListViewModel SavedMessages { get; }

    public string Subprotocols { get => Model.Subprotocols; set => Set(Model.Subprotocols, value, (m, v) => m.Subprotocols = v); }
    public decimal ListenSeconds { get => (decimal)Model.ListenSeconds; set => Set(Model.ListenSeconds, (double)value, (m, v) => m.ListenSeconds = v); }
    public decimal MaxMessages { get => Model.MaxMessages; set => Set(Model.MaxMessages, (int)value, (m, v) => m.MaxMessages = v); }

    protected override void OnLoaded()
    {
        InitialMessages.Load(Model.InitialMessages);
        SavedMessages.Load(Model.SavedMessages);
    }
}

public sealed class SocketIoEditor() : ModelEditor<SocketIoSettings>(new SocketIoSettings())
{
    public string Namespace { get => Model.Namespace; set => Set(Model.Namespace, value, (m, v) => m.Namespace = v); }
    public string Path { get => Model.Path; set => Set(Model.Path, value, (m, v) => m.Path = v); }
    public string Event { get => Model.Event; set => Set(Model.Event, value, (m, v) => m.Event = v); }
    public string Arguments { get => Model.Arguments; set => Set(Model.Arguments, value, (m, v) => m.Arguments = v); }
    public string ListenEvents { get => Model.ListenEvents; set => Set(Model.ListenEvents, value, (m, v) => m.ListenEvents = v); }
    public string AuthPayload { get => Model.AuthPayload; set => Set(Model.AuthPayload, value, (m, v) => m.AuthPayload = v); }
    public decimal ListenSeconds { get => (decimal)Model.ListenSeconds; set => Set(Model.ListenSeconds, (double)value, (m, v) => m.ListenSeconds = v); }
}

public sealed class MqttEditor() : ModelEditor<MqttSettings>(new MqttSettings())
{
    public static IReadOnlyList<MessagingMode> Modes { get; } = Enum.GetValues<MessagingMode>();
    public static IReadOnlyList<int> QosLevels { get; } = [0, 1, 2];
    public static IReadOnlyList<int> Versions { get; } = [3, 4, 5];

    public MessagingMode Mode { get => Model.Mode; set => Set(Model.Mode, value, (m, v) => m.Mode = v); }
    public string ClientId { get => Model.ClientId; set => Set(Model.ClientId, value, (m, v) => m.ClientId = v); }
    public string Topic { get => Model.Topic; set => Set(Model.Topic, value, (m, v) => m.Topic = v); }
    public string Payload { get => Model.Payload; set => Set(Model.Payload, value, (m, v) => m.Payload = v); }
    public int Qos { get => Model.Qos; set => Set(Model.Qos, value, (m, v) => m.Qos = v); }
    public bool Retain { get => Model.Retain; set => Set(Model.Retain, value, (m, v) => m.Retain = v); }
    public bool CleanSession { get => Model.CleanSession; set => Set(Model.CleanSession, value, (m, v) => m.CleanSession = v); }
    public bool UseTls { get => Model.UseTls; set => Set(Model.UseTls, value, (m, v) => m.UseTls = v); }
    public string Username { get => Model.Username; set => Set(Model.Username, value, (m, v) => m.Username = v); }
    public string Password { get => Model.Password; set => Set(Model.Password, value, (m, v) => m.Password = v); }
    public int ProtocolVersion { get => Model.ProtocolVersion; set => Set(Model.ProtocolVersion, value, (m, v) => m.ProtocolVersion = v); }
    public decimal ListenSeconds { get => (decimal)Model.ListenSeconds; set => Set(Model.ListenSeconds, (double)value, (m, v) => m.ListenSeconds = v); }
    public decimal MaxMessages { get => Model.MaxMessages; set => Set(Model.MaxMessages, (int)value, (m, v) => m.MaxMessages = v); }
}

public sealed class KafkaEditor : ModelEditor<KafkaSettings>
{
    public static IReadOnlyList<MessagingMode> Modes { get; } = Enum.GetValues<MessagingMode>();
    public static IReadOnlyList<string> OffsetResets { get; } = ["latest", "earliest"];
    public static IReadOnlyList<string> SecurityProtocols { get; } = ["PLAINTEXT", "SSL", "SASL_PLAINTEXT", "SASL_SSL"];
    public static IReadOnlyList<string> SaslMechanisms { get; } = ["PLAIN", "SCRAM-SHA-256", "SCRAM-SHA-512"];

    public KafkaEditor() : base(new KafkaSettings())
    {
        Headers = new KeyValueListViewModel("Header", "Value");
        Headers.Changed += (_, _) => { if (!IsLoading) { Model.Headers = Headers.ToItems(); RaiseChanged(); } };
    }

    public KeyValueListViewModel Headers { get; }
    public MessagingMode Mode { get => Model.Mode; set => Set(Model.Mode, value, (m, v) => m.Mode = v); }
    public string Topic { get => Model.Topic; set => Set(Model.Topic, value, (m, v) => m.Topic = v); }
    public string Key { get => Model.Key; set => Set(Model.Key, value, (m, v) => m.Key = v); }
    public string Payload { get => Model.Payload; set => Set(Model.Payload, value, (m, v) => m.Payload = v); }
    public string GroupId { get => Model.GroupId; set => Set(Model.GroupId, value, (m, v) => m.GroupId = v); }
    public string AutoOffsetReset { get => Model.AutoOffsetReset; set => Set(Model.AutoOffsetReset, value, (m, v) => m.AutoOffsetReset = v); }
    public string SecurityProtocol { get => Model.SecurityProtocol; set => Set(Model.SecurityProtocol, value, (m, v) => m.SecurityProtocol = v); }
    public string SaslMechanism { get => Model.SaslMechanism; set => Set(Model.SaslMechanism, value, (m, v) => m.SaslMechanism = v); }
    public string SaslUsername { get => Model.SaslUsername; set => Set(Model.SaslUsername, value, (m, v) => m.SaslUsername = v); }
    public string SaslPassword { get => Model.SaslPassword; set => Set(Model.SaslPassword, value, (m, v) => m.SaslPassword = v); }
    public decimal ListenSeconds { get => (decimal)Model.ListenSeconds; set => Set(Model.ListenSeconds, (double)value, (m, v) => m.ListenSeconds = v); }
    public decimal MaxMessages { get => Model.MaxMessages; set => Set(Model.MaxMessages, (int)value, (m, v) => m.MaxMessages = v); }

    protected override void OnLoaded() => Headers.Load(Model.Headers);
}

public sealed class AmqpEditor : ModelEditor<AmqpSettings>
{
    public static IReadOnlyList<MessagingMode> Modes { get; } = Enum.GetValues<MessagingMode>();

    public AmqpEditor() : base(new AmqpSettings())
    {
        Headers = new KeyValueListViewModel("Header", "Value");
        Headers.Changed += (_, _) => { if (!IsLoading) { Model.Headers = Headers.ToItems(); RaiseChanged(); } };
    }

    public KeyValueListViewModel Headers { get; }
    public MessagingMode Mode { get => Model.Mode; set => Set(Model.Mode, value, (m, v) => m.Mode = v); }
    public string Exchange { get => Model.Exchange; set => Set(Model.Exchange, value, (m, v) => m.Exchange = v); }
    public string RoutingKey { get => Model.RoutingKey; set => Set(Model.RoutingKey, value, (m, v) => m.RoutingKey = v); }
    public string Queue { get => Model.Queue; set => Set(Model.Queue, value, (m, v) => m.Queue = v); }
    public string Payload { get => Model.Payload; set => Set(Model.Payload, value, (m, v) => m.Payload = v); }
    public string ContentType { get => Model.ContentType; set => Set(Model.ContentType, value, (m, v) => m.ContentType = v); }
    public bool Persistent { get => Model.Persistent; set => Set(Model.Persistent, value, (m, v) => m.Persistent = v); }
    public decimal ListenSeconds { get => (decimal)Model.ListenSeconds; set => Set(Model.ListenSeconds, (double)value, (m, v) => m.ListenSeconds = v); }
    public decimal MaxMessages { get => Model.MaxMessages; set => Set(Model.MaxMessages, (int)value, (m, v) => m.MaxMessages = v); }

    protected override void OnLoaded() => Headers.Load(Model.Headers);
}

/// <summary>Raw TCP / UDP.</summary>
public sealed class SocketEditor() : ModelEditor<SocketSettings>(new SocketSettings())
{
    public static IReadOnlyList<PayloadEncoding> Encodings { get; } = Enum.GetValues<PayloadEncoding>();
    public static IReadOnlyList<string> LineEndings { get; } = ["", "\\n", "\\r\\n"];

    public string Payload { get => Model.Payload; set => Set(Model.Payload, value, (m, v) => m.Payload = v); }
    public PayloadEncoding Encoding { get => Model.Encoding; set => Set(Model.Encoding, value, (m, v) => m.Encoding = v); }
    public string LineEnding { get => Model.LineEnding; set => Set(Model.LineEnding, value, (m, v) => m.LineEnding = v); }
    public decimal ReadTimeoutSeconds { get => (decimal)Model.ReadTimeoutSeconds; set => Set(Model.ReadTimeoutSeconds, (double)value, (m, v) => m.ReadTimeoutSeconds = v); }
    public bool UseTls { get => Model.UseTls; set => Set(Model.UseTls, value, (m, v) => m.UseTls = v); }
}
