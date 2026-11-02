using Dispatch.Application.Variables;
using Dispatch.Domain;

namespace Dispatch.Application.Tls;

/// <summary>A host and port to inspect, with the requests that use it.</summary>
public sealed record TlsTarget(string Host, int Port, IReadOnlyList<string> RequestNames)
{
    public string Display => Port == 443 ? Host : $"{Host}:{Port}";
}

/// <summary>Finds the distinct HTTPS endpoints a collection talks to.</summary>
public static class TlsTargets
{
    public static IReadOnlyList<TlsTarget> FromCollection(RequestCollection collection, ApiEnvironment? environment = null)
    {
        var variables = VariableContext.For(environment, collection.Variables).Merged();
        var targets = new Dictionary<(string Host, int Port), List<string>>();
        foreach (var request in collection.Requests.OrderBy(r => r.SortOrder))
        {
            var url = VariableResolver.Resolve(request.Url, variables).Trim();
            if (!url.Contains("://", StringComparison.Ordinal))
                url = "https://" + url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown || uri.IdnHost.Length == 0)
                continue;
            if (uri.Scheme is not ("https" or "wss" or "grpcs"))
                continue;
            var key = (uri.IdnHost, uri.IsDefaultPort ? 443 : uri.Port);
            if (!targets.TryGetValue(key, out var names))
                targets[key] = names = [];
            names.Add(request.Name);
        }
        return targets.Select(t => new TlsTarget(t.Key.Host, t.Key.Port, t.Value)).ToList();
    }

    /// <summary>Distinct HTTPS hosts among the given URLs (used by monitors, which already have the effective URLs).</summary>
    public static IReadOnlyList<TlsTarget> FromUrls(IEnumerable<(string Name, string Url)> urls)
    {
        var targets = new Dictionary<(string Host, int Port), List<string>>();
        foreach (var (name, url) in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "wss" or "grpcs") || uri.IdnHost.Length == 0)
                continue;
            var key = (uri.IdnHost, uri.IsDefaultPort ? 443 : uri.Port);
            if (!targets.TryGetValue(key, out var names))
                targets[key] = names = [];
            names.Add(name);
        }
        return targets.Select(t => new TlsTarget(t.Key.Host, t.Key.Port, t.Value)).ToList();
    }
}
