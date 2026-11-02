using Dispatch.Application.Reporting;

namespace Dispatch.Application.Tls;

/// <summary>Plain-language findings about a TLS endpoint.</summary>
public static class TlsInsights
{
    private static readonly string[] WeakCipherWords = ["RC4", "3DES", "DES_", "NULL", "EXPORT", "MD5", "ANON"];

    public static IEnumerable<ReportInsight> For(TlsReport report, DateTimeOffset now)
    {
        if (report.Error is not null)
        {
            yield return new ReportInsight(InsightLevel.Bad, $"No TLS handshake with {report.Host}:{report.Port}: {report.Error}");
            yield break;
        }
        if (report.Leaf is not { } leaf)
        {
            yield return new ReportInsight(InsightLevel.Bad, "The server presented no certificate.");
            yield break;
        }

        // Validity.
        if (leaf.IsExpired(now))
            yield return new ReportInsight(InsightLevel.Bad, $"The certificate expired on {leaf.NotAfter:yyyy-MM-dd} ({Math.Abs(leaf.DaysToExpiry(now)):0} day(s) ago).");
        else if (leaf.IsNotYetValid(now))
            yield return new ReportInsight(InsightLevel.Bad, $"The certificate is not valid before {leaf.NotBefore:yyyy-MM-dd}.");
        else if (leaf.DaysToExpiry(now) <= report.WarnDays)
            yield return new ReportInsight(InsightLevel.Warning, $"The certificate expires in {leaf.DaysToExpiry(now):0} day(s), on {leaf.NotAfter:yyyy-MM-dd} (warning threshold: {report.WarnDays} days).");
        else
            yield return new ReportInsight(InsightLevel.Good, $"The certificate is valid for another {leaf.DaysToExpiry(now):0} day(s), until {leaf.NotAfter:yyyy-MM-dd}.");

        // Hostname and trust.
        if (!report.HostnameMatches)
            yield return new ReportInsight(InsightLevel.Bad,
                $"The certificate is not valid for {report.ServerName}. It covers: {(leaf.SubjectAlternativeNames.Count > 0 ? string.Join(", ", leaf.SubjectAlternativeNames) : leaf.CommonName)}.");
        if (!report.ChainTrusted)
        {
            yield return leaf.IsSelfSigned
                ? new ReportInsight(InsightLevel.Bad, "The certificate is self-signed: clients that verify certificates will refuse it.")
                : new ReportInsight(InsightLevel.Bad, "The chain does not lead to a trusted root on this machine" +
                                                      (report.ChainErrors.Count > 0 ? $": {string.Join("; ", report.ChainErrors)}" : ".") +
                                                      (report.Chain.Count == 1 ? " The server sent only the leaf; it may be missing an intermediate certificate." : ""));
        }
        else if (report.HostnameMatches)
            yield return new ReportInsight(InsightLevel.Good, $"The chain is trusted ({report.Chain.Count} certificate(s), issued by {leaf.IssuerName}) and valid for {report.ServerName}.");

        switch (report.Revocation)
        {
            case "revoked":
                yield return new ReportInsight(InsightLevel.Bad, "The certificate has been revoked.");
                break;
            case "unknown":
                yield return new ReportInsight(InsightLevel.Info, "Revocation status could not be determined (the OCSP responder or CRL was not reachable).");
                break;
            case "good":
                yield return new ReportInsight(InsightLevel.Good, "Revocation check passed (OCSP / CRL).");
                break;
        }

        // Protocol and cipher.
        switch (report.Protocol)
        {
            case "TLS 1.3":
                yield return new ReportInsight(InsightLevel.Good, "Negotiated TLS 1.3, the current protocol version.");
                break;
            case "TLS 1.2":
                yield return new ReportInsight(InsightLevel.Info, "Negotiated TLS 1.2. Fine today; TLS 1.3 is faster (one round trip) and drops legacy ciphers.");
                break;
            case "":
                break;
            default:
                yield return new ReportInsight(InsightLevel.Bad, $"Negotiated {report.Protocol}, which is deprecated and refused by modern clients.");
                break;
        }
        if (WeakCipherWords.Any(w => report.CipherSuite.Contains(w, StringComparison.OrdinalIgnoreCase)))
            yield return new ReportInsight(InsightLevel.Bad, $"The cipher suite {report.CipherSuite} is considered weak.");
        else if (report.CipherSuite.Contains("CBC", StringComparison.OrdinalIgnoreCase))
            yield return new ReportInsight(InsightLevel.Info, $"The cipher suite {report.CipherSuite} uses CBC; AEAD suites (GCM, CHACHA20) are preferred.");

        if (report.Alpn.Length == 0)
            yield return new ReportInsight(InsightLevel.Info, "No ALPN protocol was negotiated: the server did not offer HTTP/2 (clients fall back to HTTP/1.1).");
        else if (report.Alpn == "h2")
            yield return new ReportInsight(InsightLevel.Good, "HTTP/2 is available (ALPN h2).");
        else
            yield return new ReportInsight(InsightLevel.Info, $"ALPN negotiated {report.Alpn}; HTTP/2 is not offered.");

        // Key and signature.
        if (leaf.PublicKey.StartsWith("RSA ", StringComparison.Ordinal) && int.TryParse(leaf.PublicKey[4..], out var bits) && bits < 2048)
            yield return new ReportInsight(InsightLevel.Warning, $"The {leaf.PublicKey} key is shorter than the 2048-bit minimum.");
        if (leaf.SignatureAlgorithm.Contains("sha1", StringComparison.OrdinalIgnoreCase) || leaf.SignatureAlgorithm.Contains("md5", StringComparison.OrdinalIgnoreCase))
            yield return new ReportInsight(InsightLevel.Bad, $"The certificate is signed with {leaf.SignatureAlgorithm}, which browsers no longer accept.");
        if (leaf.SubjectAlternativeNames.Count == 0)
            yield return new ReportInsight(InsightLevel.Warning, "The certificate has no Subject Alternative Names; clients ignore the Common Name and will reject it.");
        if (leaf.Validity.TotalDays > 398)
            yield return new ReportInsight(InsightLevel.Info, $"The certificate was issued for {leaf.Validity.TotalDays:0} days; browsers distrust public certificates valid for more than 398 days.");
        if (report.HandshakeTime is { } handshake && handshake.TotalMilliseconds > 500)
            yield return new ReportInsight(InsightLevel.Info, $"The handshake took {handshake.TotalMilliseconds:0} ms.");
    }
}
