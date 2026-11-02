using System.Collections.Concurrent;
using Dispatch.Application.Tls;

namespace Dispatch.App.Services;

/// <summary>A short TLS summary for the response timeline.</summary>
public sealed record TlsSummary(string Host, int Port, TlsReport Report)
{
    public string Protocol => Report.Error is null ? Report.Protocol : "no handshake";
    public string Cipher => Report.CipherSuite;
    public string Alpn => Report.Alpn.Length > 0 ? Report.Alpn : "none";
    public string Issuer => Report.Leaf?.IssuerName ?? "";
    public string Expiry => Report.DaysToExpiry is { } d
        ? d < 0 ? $"expired {Math.Abs(d):0} day(s) ago" : $"expires in {d:0} day(s) ({Report.Leaf!.NotAfter:yyyy-MM-dd})"
        : "";
    public string Line => Report.Error is not null
        ? $"{Host}:{Port} · {Report.Error}"
        : $"{Protocol} · {Cipher} · ALPN {Alpn}";
    public string CertificateLine => Report.Error is not null || Report.Leaf is null
        ? ""
        : $"{Report.Leaf.CommonName} · issued by {Issuer} · {Expiry}" + (Report.ChainTrusted ? "" : " · chain not trusted") + (Report.HostnameMatches ? "" : " · hostname mismatch");
    public TlsVerdict Verdict => Report.Verdict;
    public IReadOnlyList<string> Problems => Report.Problems;
    public bool HasProblems => Problems.Count > 0;
}

/// <summary>
/// Looks up the TLS details of a host once and keeps them for a while, so the response timeline can show the
/// certificate and protocol of every HTTPS send without a second handshake per request.
/// </summary>
public static class TlsSummaryCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, Task<TlsSummary?>> Entries = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Stamps = new();
    private static readonly ITlsInspector Inspector = new TlsInspector();

    /// <summary>The TLS summary for an HTTPS URL, or null when the URL is not HTTPS.</summary>
    public static Task<TlsSummary?> LookupAsync(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.IdnHost.Length == 0)
            return Task.FromResult<TlsSummary?>(null);
        var host = uri.IdnHost;
        var port = uri.IsDefaultPort ? 443 : uri.Port;
        var key = $"{host}:{port}";
        if (Stamps.TryGetValue(key, out var at) && DateTimeOffset.UtcNow - at > Lifetime)
        {
            Entries.TryRemove(key, out _);
            Stamps.TryRemove(key, out _);
        }
        return Entries.GetOrAdd(key, _ =>
        {
            Stamps[key] = DateTimeOffset.UtcNow;
            return InspectAsync(host, port);
        });
    }

    private static async Task<TlsSummary?> InspectAsync(string host, int port)
    {
        try
        {
            var report = await Inspector.InspectAsync(host, port, new TlsInspectOptions { Timeout = TimeSpan.FromSeconds(8), CheckRevocation = false });
            return new TlsSummary(host, port, report);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Clear()
    {
        Entries.Clear();
        Stamps.Clear();
    }
}
