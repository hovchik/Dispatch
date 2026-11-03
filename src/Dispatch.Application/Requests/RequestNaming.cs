using Dispatch.Domain;

namespace Dispatch.Application.Requests;

/// <summary>
/// Names for requests the user has not named yet. A new tab starts as "New Request"; once it is sent
/// the tab takes a name derived from what was sent (e.g. <c>GET /users</c>) so a row of tabs stays readable.
/// </summary>
public static class RequestNaming
{
    public const string DefaultName = "New Request";

    /// <summary>The placeholder a new tab of this kind starts with: "New Request" for HTTP, "New GraphQL request", …</summary>
    public static string DefaultNameFor(RequestKind kind) =>
        kind == RequestKind.Http ? DefaultName : $"New {KindLabel(kind)} request";

    private static readonly HashSet<string> DefaultNames = new(
        Enum.GetValues<RequestKind>().Select(DefaultNameFor).Append("Untitled"),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True only for the placeholder names Dispatch generates itself ("New Request", "New GraphQL request",
    /// their "(copy)" duplicates, "Untitled") and for blank names. A name the user typed that happens to
    /// look similar ("New customer request") is not a default name.
    /// </summary>
    public static bool IsDefaultName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;
        var trimmed = name.Trim();
        while (trimmed.EndsWith("(copy)", StringComparison.Ordinal))
            trimmed = trimmed[..^"(copy)".Length].TrimEnd();
        return DefaultNames.Contains(trimmed);
    }

    private static string KindLabel(RequestKind kind) => kind switch
    {
        RequestKind.Http => "HTTP",
        RequestKind.GraphQl => "GraphQL",
        RequestKind.Grpc => "gRPC",
        RequestKind.Soap => "SOAP",
        RequestKind.WebSocket => "WebSocket",
        RequestKind.Sse => "Server-Sent Events",
        RequestKind.SocketIo => "Socket.IO",
        RequestKind.Mqtt => "MQTT",
        RequestKind.Kafka => "Kafka",
        RequestKind.Amqp => "AMQP (RabbitMQ)",
        RequestKind.Tcp => "TCP",
        RequestKind.Udp => "UDP",
        _ => kind.ToString()
    };

    /// <summary>
    /// A short name describing the request, or null when there is nothing to name it after (no URL).
    /// HTTP: <c>GET /users/{id}</c>; GraphQL: the operation name; gRPC: <c>Greeter/SayHello</c>;
    /// SOAP: the operation; messaging: the topic/queue; streams and sockets: the endpoint.
    /// </summary>
    public static string? Suggest(ApiRequest request)
    {
        var url = request.Url.Trim();
        var endpoint = Endpoint(url);

        var name = request.Kind switch
        {
            RequestKind.Http => Join(request.Method.ToString().ToUpperInvariant(), endpoint),
            RequestKind.GraphQl => First(request.Protocol.GraphQl.OperationName, Join("GraphQL", endpoint)),
            RequestKind.Grpc => GrpcName(request.Protocol.Grpc) ?? Join("gRPC", endpoint),
            RequestKind.Soap => First(request.Protocol.Soap.Operation, Join("SOAP", endpoint)),
            RequestKind.WebSocket => Join("WS", endpoint),
            RequestKind.Sse => Join("SSE", endpoint),
            RequestKind.SocketIo => Join("Socket.IO", endpoint),
            RequestKind.Mqtt => Join("MQTT", First(request.Protocol.Mqtt.Topic, endpoint)),
            RequestKind.Kafka => Join("Kafka", First(request.Protocol.Kafka.Topic, endpoint)),
            RequestKind.Amqp => Join("AMQP", First(request.Protocol.Amqp.Queue, request.Protocol.Amqp.RoutingKey,
                request.Protocol.Amqp.Exchange, endpoint)),
            RequestKind.Tcp => Join("TCP", endpoint),
            RequestKind.Udp => Join("UDP", endpoint),
            _ => null
        };

        return string.IsNullOrWhiteSpace(name) ? null : Truncate(name.Trim());
    }

    private static string? GrpcName(GrpcSettings grpc)
    {
        if (string.IsNullOrWhiteSpace(grpc.Method))
            return null;
        var service = grpc.Service.Trim();
        var dot = service.LastIndexOf('.');
        if (dot >= 0)
            service = service[(dot + 1)..];
        return service.Length == 0 ? grpc.Method.Trim() : $"{service}/{grpc.Method.Trim()}";
    }

    /// <summary>
    /// The path of the URL, or its host when the path is empty, so names stay short: <c>/users</c> for
    /// <c>https://api.example.com/users?page=2</c>, <c>api.example.com</c> for <c>https://api.example.com/</c>.
    /// Works on unresolved URLs too: <c>{{baseUrl}}/users</c> gives <c>/users</c>.
    /// </summary>
    private static string? Endpoint(string url)
    {
        if (url.Length == 0)
            return null;

        var (withoutQuery, _, _) = QueryString.Split(url);
        var rest = withoutQuery;
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            rest = rest[(scheme + 3)..];

        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? string.Empty : rest[slash..].TrimEnd('/');

        if (path.Length > 0)
            return path;
        return authority.Length > 0 ? authority : withoutQuery;
    }

    private static string? First(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    private static string? Join(string prefix, string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? null : $"{prefix} {endpoint.Trim()}";

    private static string Truncate(string name) => name.Length <= 60 ? name : name[..59] + "…";
}
