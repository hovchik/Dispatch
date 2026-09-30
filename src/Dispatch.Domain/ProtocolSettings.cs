using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dispatch.Domain;

/// <summary>
/// Protocol-specific configuration. Only the section matching <see cref="ApiRequest.Kind"/> is used, but all are kept so
/// switching protocols back and forth never loses what the user typed.
/// </summary>
public sealed class ProtocolSettings
{
    public GraphQlSettings GraphQl { get; set; } = new();
    public GrpcSettings Grpc { get; set; } = new();
    public SoapSettings Soap { get; set; } = new();
    public StreamSettings Stream { get; set; } = new();
    public SocketIoSettings SocketIo { get; set; } = new();
    public MqttSettings Mqtt { get; set; } = new();
    public KafkaSettings Kafka { get; set; } = new();
    public AmqpSettings Amqp { get; set; } = new();
    public SocketSettings Socket { get; set; } = new();

    public ProtocolSettings Clone() => DeepCopy.Of(this);
}

public sealed class GraphQlSettings
{
    public string Query { get; set; } = string.Empty;
    public string Variables { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;

    /// <summary>Subscriptions go over WebSocket (graphql-transport-ws); empty derives ws(s):// from the URL.</summary>
    public string SubscriptionUrl { get; set; } = string.Empty;
}

public enum GrpcSchemaSource
{
    ServerReflection,
    ProtoFiles
}

public sealed class GrpcSettings
{
    public GrpcSchemaSource SchemaSource { get; set; } = GrpcSchemaSource.ServerReflection;

    /// <summary>Paths of .proto files (imports are resolved relative to each file and <see cref="ImportPaths"/>).</summary>
    public List<string> ProtoFiles { get; set; } = [];
    public List<string> ImportPaths { get; set; } = [];

    /// <summary>Fully-qualified service name, e.g. <c>helloworld.Greeter</c>.</summary>
    public string Service { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;

    /// <summary>Request message as JSON (proto3 JSON mapping). For client streaming, a JSON array of messages.</summary>
    public string Message { get; set; } = "{}";

    public bool UseTls { get; set; }
    public double DeadlineSeconds { get; set; }
}

public enum SoapVersion
{
    Soap11,
    Soap12
}

public enum WsSecurityMode
{
    None,
    UsernameTokenText,
    UsernameTokenDigest
}

public sealed class SoapSettings
{
    public string WsdlUrl { get; set; } = string.Empty;
    public SoapVersion Version { get; set; } = SoapVersion.Soap11;
    public string Action { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public WsSecurityMode WsSecurity { get; set; } = WsSecurityMode.None;
    public string WsUsername { get; set; } = string.Empty;
    public string WsPassword { get; set; } = string.Empty;
    public bool AddTimestamp { get; set; }
}

/// <summary>Shared by WebSocket and Server-Sent Events.</summary>
public sealed class StreamSettings
{
    /// <summary>Messages sent right after connecting (WebSocket only).</summary>
    public List<KeyValueItem> InitialMessages { get; set; } = [];

    /// <summary>Saved messages the user can send with one click while connected (key = label, value = payload).</summary>
    public List<KeyValueItem> SavedMessages { get; set; } = [];

    public string Subprotocols { get; set; } = string.Empty;

    /// <summary>How long a non-interactive run (collection runner / CLI) listens before closing. 0 = until closed.</summary>
    public double ListenSeconds { get; set; } = 5;

    /// <summary>Stop after this many received messages (0 = no limit).</summary>
    public int MaxMessages { get; set; }
}

public sealed class SocketIoSettings
{
    public string Namespace { get; set; } = "/";
    public string Path { get; set; } = "/socket.io/";
    public string Event { get; set; } = "message";

    /// <summary>Event arguments as a JSON array (or a single JSON value).</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Comma-separated event names to show; empty shows all events.</summary>
    public string ListenEvents { get; set; } = string.Empty;
    public string AuthPayload { get; set; } = string.Empty;
    public double ListenSeconds { get; set; } = 5;
}

public enum MessagingMode
{
    Publish,
    Subscribe,
    PublishAndSubscribe
}

public sealed class MqttSettings
{
    public MessagingMode Mode { get; set; } = MessagingMode.Publish;
    public string ClientId { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public int Qos { get; set; }
    public bool Retain { get; set; }
    public bool CleanSession { get; set; } = true;
    public bool UseTls { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; } = 5;
    public double ListenSeconds { get; set; } = 5;
    public int MaxMessages { get; set; }
}

public sealed class KafkaSettings
{
    public MessagingMode Mode { get; set; } = MessagingMode.Publish;
    public string Topic { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public List<KeyValueItem> Headers { get; set; } = [];
    public string GroupId { get; set; } = "dispatch";

    /// <summary>earliest | latest</summary>
    public string AutoOffsetReset { get; set; } = "latest";

    /// <summary>PLAINTEXT | SSL | SASL_PLAINTEXT | SASL_SSL</summary>
    public string SecurityProtocol { get; set; } = "PLAINTEXT";

    /// <summary>PLAIN | SCRAM-SHA-256 | SCRAM-SHA-512</summary>
    public string SaslMechanism { get; set; } = "PLAIN";
    public string SaslUsername { get; set; } = string.Empty;
    public string SaslPassword { get; set; } = string.Empty;
    public double ListenSeconds { get; set; } = 10;
    public int MaxMessages { get; set; }
}

public sealed class AmqpSettings
{
    public MessagingMode Mode { get; set; } = MessagingMode.Publish;
    public string Exchange { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
    public string Queue { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/json";
    public List<KeyValueItem> Headers { get; set; } = [];
    public bool Persistent { get; set; }
    public double ListenSeconds { get; set; } = 5;
    public int MaxMessages { get; set; }
}

public enum PayloadEncoding
{
    Text,
    Hex,
    Base64
}

/// <summary>Raw TCP / UDP.</summary>
public sealed class SocketSettings
{
    public string Payload { get; set; } = string.Empty;
    public PayloadEncoding Encoding { get; set; } = PayloadEncoding.Text;

    /// <summary>Appended to text payloads: none, \n or \r\n.</summary>
    public string LineEnding { get; set; } = string.Empty;
    public double ReadTimeoutSeconds { get; set; } = 3;
    public bool UseTls { get; set; }
}

/// <summary>Per-request transport options.</summary>
public sealed class RequestSettings
{
    /// <summary>0 = default (100 s).</summary>
    public int TimeoutMs { get; set; }
    public bool FollowRedirects { get; set; } = true;
    public int MaxRedirects { get; set; } = 10;
    public bool VerifySsl { get; set; } = true;
    public HttpVersionPreference HttpVersion { get; set; } = HttpVersionPreference.Auto;

    /// <summary>e.g. http://proxy:8080; empty uses the system proxy.</summary>
    public string Proxy { get; set; } = string.Empty;
    public bool UseSystemProxy { get; set; } = true;

    /// <summary>Client certificate (PFX/P12 or PEM) for mutual TLS.</summary>
    public string ClientCertificatePath { get; set; } = string.Empty;
    public string ClientCertificateKeyPath { get; set; } = string.Empty;
    public string ClientCertificatePassword { get; set; } = string.Empty;

    /// <summary>Send cookies from, and store Set-Cookie into, the shared cookie jar.</summary>
    public bool UseCookieJar { get; set; } = true;

    public RequestSettings Clone() => (RequestSettings)MemberwiseClone();

    /// <summary>A key that identifies the transport configuration, for pooling HTTP handlers.</summary>
    public string TransportKey() =>
        $"{FollowRedirects}|{MaxRedirects}|{VerifySsl}|{HttpVersion}|{Proxy}|{UseSystemProxy}|{ClientCertificatePath}|{ClientCertificateKeyPath}|{ClientCertificatePassword.GetHashCode()}";
}

public static class DeepCopy
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Deep copy via JSON; fine for small settings objects.</summary>
    public static T Of<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
