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
        return new BrokerAddress(uri.Scheme.ToLowerInvariant(), uri.Host, uri.IsDefaultPort || uri.Port <= 0 ? defaultPort : uri.Port,
            user, password, uri.AbsolutePath);
    }

    public override string ToString() => $"{Scheme}://{Host}:{Port}{(Path is "/" or "" ? "" : Path)}";
}
