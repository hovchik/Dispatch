namespace Dispatch.Domain;

/// <summary>The protocol a request speaks. Each kind has its own executor and editor.</summary>
public enum RequestKind
{
    Http,
    GraphQl,
    Grpc,
    Soap,
    WebSocket,
    Sse,
    SocketIo,
    Mqtt,
    Kafka,
    Amqp,
    Tcp,
    Udp
}

public enum HttpVerb
{
    Get,
    Post,
    Put,
    Patch,
    Delete,
    Head,
    Options
}

public enum BodyMode
{
    None,
    Json,
    Text,
    Xml,
    FormUrlEncoded,
    Multipart,
    Binary
}

public enum AuthMode
{
    None,
    Bearer,
    Basic,
    ApiKey,
    OAuth2,
    AwsSigV4,
    Digest,
    Ntlm
}

public enum ApiKeyLocation
{
    Header,
    QueryParam
}

public enum OAuth2GrantType
{
    ClientCredentials,
    Password,
    AuthorizationCode,
    DeviceCode
}

public enum HttpVersionPreference
{
    Auto,
    Http11,
    Http2,
    Http3
}

/// <summary>Direction of a message in a streaming session (WebSocket, gRPC stream, MQTT, ...).</summary>
public enum MessageDirection
{
    Sent,
    Received,
    Info,
    Error
}
