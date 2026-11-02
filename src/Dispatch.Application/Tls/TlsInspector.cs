using System.ComponentModel;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dispatch.Application.Reporting;

namespace Dispatch.Application.Tls;

public sealed class TlsInspectOptions
{
    /// <summary>Port when the target names none.</summary>
    public int DefaultPort { get; init; } = 443;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Certificates expiring within this many days are reported as a warning.</summary>
    public int WarnDays { get; init; } = 21;

    /// <summary>Check revocation (OCSP / CRL) online; off makes the check "not checked".</summary>
    public bool CheckRevocation { get; init; } = true;

    /// <summary>Protocols offered in ALPN, so the report can say whether HTTP/2 is available.</summary>
    public IReadOnlyList<string> Alpn { get; init; } = ["h2", "http/1.1"];

    /// <summary>Server name for SNI and hostname checks; defaults to the host.</summary>
    public string? ServerName { get; init; }

    /// <summary>An HTTP proxy to tunnel through (CONNECT), e.g. <c>http://proxy:3128</c>; empty = direct or system proxy.</summary>
    public string? Proxy { get; init; }

    /// <summary>Use the system proxy for the host when <see cref="Proxy"/> is empty.</summary>
    public bool UseSystemProxy { get; init; }
}

/// <summary>One certificate of the presented chain, leaf first.</summary>
public sealed record TlsCertificateInfo(
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    IReadOnlyList<string> SubjectAlternativeNames,
    string Thumbprint,
    string SerialNumber,
    string SignatureAlgorithm,
    string PublicKey,
    bool IsCertificateAuthority,
    bool IsSelfSigned)
{
    public double DaysToExpiry(DateTimeOffset now) => (NotAfter - now).TotalDays;
    public bool IsExpired(DateTimeOffset now) => now > NotAfter;
    public bool IsNotYetValid(DateTimeOffset now) => now < NotBefore;
    public TimeSpan Validity => NotAfter - NotBefore;

    /// <summary>The CN of the subject, or the whole subject when there is none.</summary>
    public string CommonName => Dn(Subject);

    public string IssuerName => Dn(Issuer);

    internal static string Dn(string distinguishedName)
    {
        foreach (var part in distinguishedName.Split(',', StringSplitOptions.TrimEntries))
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return part[3..].Trim();
        return distinguishedName;
    }
}

public enum TlsVerdict
{
    Ok,
    Warning,
    Bad
}

/// <summary>What one TLS handshake with a host revealed.</summary>
public sealed class TlsReport
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 443;
    public string ServerName { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Duration { get; set; }
    public TimeSpan? HandshakeTime { get; set; }

    /// <summary>Set when no handshake completed (connection refused, timeout, protocol mismatch, ...).</summary>
    public string? Error { get; set; }

    public string Protocol { get; set; } = string.Empty;
    public string CipherSuite { get; set; } = string.Empty;

    /// <summary>The negotiated ALPN protocol ("h2", "http/1.1"), or empty when the server picked none.</summary>
    public string Alpn { get; set; } = string.Empty;
    public List<TlsCertificateInfo> Chain { get; } = [];
    public TlsCertificateInfo? Leaf => Chain.Count > 0 ? Chain[0] : null;

    /// <summary>Whether the leaf is valid for <see cref="ServerName"/>.</summary>
    public bool HostnameMatches { get; set; }

    /// <summary>Whether the chain builds up to a trusted root on this machine.</summary>
    public bool ChainTrusted { get; set; }
    public List<string> ChainErrors { get; } = [];

    /// <summary>"good", "revoked", "unknown" (the responder could not be reached) or "not checked".</summary>
    public string Revocation { get; set; } = "not checked";

    public double? DaysToExpiry { get; set; }
    public int WarnDays { get; set; } = 21;
    public List<ReportInsight> Insights { get; } = [];

    public TlsVerdict Verdict =>
        Error is not null || Insights.Any(i => i.Level == InsightLevel.Bad) ? TlsVerdict.Bad
        : Insights.Any(i => i.Level == InsightLevel.Warning) ? TlsVerdict.Warning
        : TlsVerdict.Ok;

    public bool Passed => Verdict == TlsVerdict.Ok;

    /// <summary>Problems a CI gate cares about (bad and warning insights, or the error).</summary>
    public IReadOnlyList<string> Problems =>
        Error is not null ? [Error] : Insights.Where(i => i.Level is InsightLevel.Bad or InsightLevel.Warning).Select(i => i.Text).ToList();

    public string Summary
    {
        get
        {
            if (Error is not null)
                return $"{Host}:{Port}: {Error}";
            var expiry = DaysToExpiry is { } d
                ? d < 0 ? $"expired {Math.Abs(d):0} day(s) ago" : $"expires in {d:0} day(s)"
                : "no certificate";
            return $"{Protocol} · {CipherSuite}" + (Alpn.Length > 0 ? $" · {Alpn}" : "") + $" · {expiry}" +
                   (ChainTrusted ? "" : " · chain not trusted") + (HostnameMatches ? "" : " · hostname mismatch");
        }
    }
}

/// <summary>Inspects a host's TLS endpoint. Implemented over <see cref="SslStream"/>; replaceable in tests.</summary>
public interface ITlsInspector
{
    Task<TlsReport> InspectAsync(string host, int port, TlsInspectOptions? options = null, CancellationToken ct = default);
}

/// <summary>
/// Opens one TLS connection to a host and records what the handshake negotiated: protocol version, cipher suite, ALPN,
/// the presented certificate chain, hostname and trust problems, revocation status and days to expiry. Only ever
/// reads; no request is sent over the connection.
/// </summary>
public sealed class TlsInspector : ITlsInspector
{
    /// <summary>Splits "host:port", "[::1]:8443" or "https://host/path" into host and port (defaulting to <paramref name="defaultPort"/>).</summary>
    public static bool TryParseTarget(string target, int defaultPort, out string host, out int port)
    {
        host = "";
        port = defaultPort;
        var text = target.Trim();
        if (text.Length == 0)
            return false;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown || uri.IdnHost.Length == 0)
            return false;
        host = uri.IdnHost;
        port = uri.IsDefaultPort ? defaultPort : uri.Port;
        return true;
    }

    public async Task<TlsReport> InspectAsync(string host, int port, TlsInspectOptions? options = null, CancellationToken ct = default)
    {
        options ??= new TlsInspectOptions();
        var serverName = string.IsNullOrWhiteSpace(options.ServerName) ? host : options.ServerName.Trim();
        var report = new TlsReport { Host = host, Port = port, ServerName = serverName, WarnDays = options.WarnDays };
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);

        X509Certificate2? presented = null;
        X509Chain? presentedChain = null;
        var policyErrors = SslPolicyErrors.None;
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            var proxy = ResolveProxy(options, host, port);
            if (proxy is null)
                await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            else
            {
                await tcp.ConnectAsync(proxy.IdnHost, proxy.Port, timeout.Token).ConfigureAwait(false);
                await OpenTunnelAsync(tcp.GetStream(), host, port, timeout.Token).ConfigureAwait(false);
            }
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, certificate, chain, errors) =>
            {
                // Record everything and accept: the point is to report problems, not to refuse the handshake.
                if (certificate is not null)
                    presented = new X509Certificate2(certificate);
                presentedChain = chain;
                policyErrors = errors;
                if (chain is not null)
                    foreach (var element in chain.ChainElements)
                        report.Chain.Add(Describe(element.Certificate));
                return true;
            });
            var handshake = Stopwatch.StartNew();
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = serverName,
                ApplicationProtocols = options.Alpn.Select(p => new SslApplicationProtocol(p)).ToList(),
                CertificateRevocationCheckMode = options.CheckRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption
            }, timeout.Token).ConfigureAwait(false);
            report.HandshakeTime = handshake.Elapsed;
            report.Protocol = ProtocolName(ssl.SslProtocol);
            report.CipherSuite = CipherName(ssl);
            report.Alpn = ssl.NegotiatedApplicationProtocol.ToString();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            report.Error = $"Timed out after {options.Timeout.TotalSeconds:0} s.";
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or Win32Exception)
        {
            report.Error = ex.InnerException is not null && ex.Message.Length < 40 ? $"{ex.Message} {ex.InnerException.Message}" : ex.Message;
        }
        report.Duration = stopwatch.Elapsed;

        if (presented is not null && report.Chain.Count == 0)
            report.Chain.Add(Describe(presented));

        var now = DateTimeOffset.UtcNow;
        if (report.Leaf is { } leaf)
        {
            report.DaysToExpiry = leaf.DaysToExpiry(now);
            report.HostnameMatches = !policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
            report.ChainTrusted = !policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) && !policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable);
            if (presentedChain is not null)
            {
                foreach (var status in presentedChain.ChainStatus)
                    if (status.Status != X509ChainStatusFlags.NoError)
                        report.ChainErrors.Add($"{status.Status}: {status.StatusInformation.Trim()}");
                report.Revocation = RevocationStatus(presentedChain, options.CheckRevocation);
            }
        }
        report.Insights.AddRange(TlsInsights.For(report, now));
        presented?.Dispose();
        return report;
    }

    /// <summary>The proxy to tunnel through: the explicit one, else the system proxy when asked, else none (direct).</summary>
    internal static Uri? ResolveProxy(TlsInspectOptions options, string host, int port)
    {
        if (!string.IsNullOrWhiteSpace(options.Proxy))
        {
            var text = options.Proxy.Trim();
            if (!text.Contains("://", StringComparison.Ordinal))
                text = "http://" + text;
            return Uri.TryCreate(text, UriKind.Absolute, out var explicitProxy) ? explicitProxy : throw new IOException($"'{options.Proxy}' is not a proxy address.");
        }
        if (!options.UseSystemProxy)
            return null;
        var target = new Uri($"https://{(host.Contains(':') ? "[" + host + "]" : host)}:{port}/");
        var system = HttpClient.DefaultProxy;
        return system.IsBypassed(target) ? null : system.GetProxy(target);
    }

    /// <summary>Asks an HTTP proxy for a tunnel to the host (<c>CONNECT host:port</c>) and drains its response head.</summary>
    private static async Task OpenTunnelAsync(NetworkStream stream, string host, int port, CancellationToken ct)
    {
        var authority = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
        var request = System.Text.Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Connection: keep-alive\r\n\r\n");
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        // Read the status line and headers byte by byte so nothing of the TLS handshake that follows is swallowed.
        var head = new System.Text.StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
                throw new IOException("The proxy closed the connection before answering CONNECT.");
            head.Append((char)one[0]);
            if (head.Length > 16 * 1024)
                throw new IOException("The proxy's CONNECT response is too long.");
        }
        var statusLine = head.ToString().Split("\r\n")[0];
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var status) || status is < 200 or > 299)
            throw new IOException($"The proxy refused the tunnel: {statusLine}");
    }

    private static string RevocationStatus(X509Chain chain, bool checkedOnline)
    {
        if (!checkedOnline)
            return "not checked";
        var flags = chain.ChainStatus.Select(s => s.Status).Aggregate(X509ChainStatusFlags.NoError, (a, b) => a | b);
        if (flags.HasFlag(X509ChainStatusFlags.Revoked))
            return "revoked";
        if (flags.HasFlag(X509ChainStatusFlags.RevocationStatusUnknown) || flags.HasFlag(X509ChainStatusFlags.OfflineRevocation))
            return "unknown";
        return "good";
    }

    internal static TlsCertificateInfo Describe(X509Certificate2 certificate)
    {
        var sans = new List<string>();
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                sans.AddRange(san.EnumerateDnsNames());
                sans.AddRange(san.EnumerateIPAddresses().Select(ip => ip.ToString()));
            }
        }
        var isCa = certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority);
        return new TlsCertificateInfo(
            certificate.Subject,
            certificate.Issuer,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
            sans,
            certificate.Thumbprint,
            certificate.SerialNumber,
            certificate.SignatureAlgorithm.FriendlyName ?? certificate.SignatureAlgorithm.Value ?? "",
            PublicKeyName(certificate),
            isCa,
            certificate.Subject == certificate.Issuer);
    }

    private static string PublicKeyName(X509Certificate2 certificate)
    {
        try
        {
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is not null)
                return $"RSA {rsa.KeySize}";
            using var ecdsa = certificate.GetECDsaPublicKey();
            if (ecdsa is not null)
                return $"ECDSA {ecdsa.KeySize}";
        }
        catch (CryptographicException)
        {
        }
        return certificate.PublicKey.Oid.FriendlyName ?? certificate.PublicKey.Oid.Value ?? "unknown";
    }

    private static string ProtocolName(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
#pragma warning disable CS0618 // older protocols are reported, never requested
        SslProtocols.Tls11 => "TLS 1.1",
        SslProtocols.Tls => "TLS 1.0",
        SslProtocols.Ssl3 => "SSL 3.0",
        SslProtocols.Ssl2 => "SSL 2.0",
#pragma warning restore CS0618
        _ => protocol.ToString()
    };

    private static string CipherName(SslStream ssl)
    {
        try
        {
            return ssl.NegotiatedCipherSuite.ToString();
        }
        catch (PlatformNotSupportedException)
        {
#pragma warning disable SYSLIB0058 // fallback for platforms without NegotiatedCipherSuite
            return $"{ssl.CipherAlgorithm} {ssl.CipherStrength}";
#pragma warning restore SYSLIB0058
        }
    }
}
