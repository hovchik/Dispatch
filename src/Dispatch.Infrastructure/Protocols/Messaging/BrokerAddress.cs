using Dispatch.Application.Requests;

namespace Dispatch.Infrastructure.Protocols.Messaging;

/// <summary>A parsed broker endpoint such as <c>mqtts://user:pass@host:8883</c> or <c>tcp://host:9000</c>.</summary>
public sealed record BrokerAddress(string Scheme, string Host, int Port, string? UserName, string? Password, string Path)
{
    public static BrokerAddress Parse(string url, string defaultScheme, int defaultPort)
    {
        url = url.Trim();
        if (url.Length == 0)
            throw new RequestBuildException("Enter the server address.");
        if (!url.Contains("://", StringComparison.Ordinal))
            url = defaultScheme + "://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            throw new RequestBuildException($"Invalid address: {url}");

        string? user = null, password = null;
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        }
        // Uri.Port is -1 for schemes it doesn't know (mqtt, amqp, ...) and the scheme's own default for ws/wss (80/443),
        // which must win over the broker default: an omitted port on ws://host means 80, not 1883.
        return new BrokerAddress(uri.Scheme.ToLowerInvariant(), uri.Host, uri.Port > 0 ? uri.Port : defaultPort,
            user, password, uri.AbsolutePath);
    }

    public override string ToString() => $"{Scheme}://{Host}:{Port}{(Path is "/" or "" ? "" : Path)}";
}
