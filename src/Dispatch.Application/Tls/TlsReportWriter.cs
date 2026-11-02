using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Application.Reporting;
using static Dispatch.Application.Reporting.ReportHtml;

namespace Dispatch.Application.Tls;

/// <summary>Text, HTML and JSON renderings of one or more <see cref="TlsReport"/>s.</summary>
public static class TlsReportWriter
{
    public static string Text(TlsReport report) => Text([report]);

    public static string Text(IReadOnlyList<TlsReport> reports)
    {
        var sb = new StringBuilder();
        foreach (var report in reports)
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine($"TLS · {report.Host}:{report.Port}" + (report.ServerName != report.Host ? $" (SNI {report.ServerName})" : ""));
            sb.AppendLine($"  {Marker(report.Verdict)} {report.Summary}");
            if (report.Error is null)
            {
                sb.AppendLine($"  protocol         {report.Protocol}");
                sb.AppendLine($"  cipher           {report.CipherSuite}");
                sb.AppendLine($"  alpn             {(report.Alpn.Length > 0 ? report.Alpn : "none")}");
                sb.AppendLine($"  revocation       {report.Revocation}");
                if (report.HandshakeTime is { } handshake)
                    sb.AppendLine($"  handshake        {handshake.TotalMilliseconds:0} ms");
                for (var i = 0; i < report.Chain.Count; i++)
                {
                    var c = report.Chain[i];
                    sb.AppendLine($"  {(i == 0 ? "chain" : ""),-16} {i + 1}. {c.CommonName}  ← {c.IssuerName}");
                    sb.AppendLine($"  {"",-16}    {c.NotBefore:yyyy-MM-dd} → {c.NotAfter:yyyy-MM-dd} · {c.PublicKey} · {c.SignatureAlgorithm}" +
                                  (c.IsSelfSigned ? " · self-signed" : "") + (c.IsCertificateAuthority ? " · CA" : ""));
                    if (i == 0 && c.SubjectAlternativeNames.Count > 0)
                        sb.AppendLine($"  {"",-16}    SAN: {string.Join(", ", c.SubjectAlternativeNames.Take(12))}{(c.SubjectAlternativeNames.Count > 12 ? ", …" : "")}");
                }
            }
            if (report.Insights.Count > 0)
            {
                sb.AppendLine();
                foreach (var insight in report.Insights)
                    sb.AppendLine($"  {Marker(insight.Level)} {insight.Text}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Marker(InsightLevel level) => level switch
    {
        InsightLevel.Good => "✓",
        InsightLevel.Bad => "✗",
        InsightLevel.Warning => "!",
        _ => "·"
    };

    private static string Marker(TlsVerdict verdict) => verdict switch
    {
        TlsVerdict.Ok => "✓",
        TlsVerdict.Warning => "!",
        _ => "✗"
    };

    public static string Html(TlsReport report) => Html([report]);

    public static string Html(IReadOnlyList<TlsReport> reports)
    {
        var worst = reports.Count == 0 ? TlsVerdict.Ok : reports.Max(r => r.Verdict);
        var title = reports.Count == 1 ? $"TLS · {reports[0].Host}:{reports[0].Port}" : $"TLS · {reports.Count} hosts";
        var sb = Begin(title, title, worst.ToString().ToUpperInvariant(), worst == TlsVerdict.Ok ? true : worst == TlsVerdict.Bad ? false : null);
        sb.Append($"<p class=\"muted\">{reports.FirstOrDefault()?.StartedAt:yyyy-MM-dd HH:mm:ss}</p>");
        if (reports.Count > 1)
        {
            sb.Append("<div class=\"card\"><table><thead><tr><th>Host</th><th>Verdict</th><th>Protocol</th><th>Expires</th><th>Issuer</th></tr></thead><tbody>");
            foreach (var r in reports)
                sb.Append($"<tr><td><a href=\"#{E(Anchor(r))}\">{E(r.Host)}:{r.Port}</a></td><td style=\"color:{VerdictColour(r.Verdict)}\">{r.Verdict}</td>" +
                          $"<td>{E(r.Protocol)}</td><td>{(r.DaysToExpiry is { } d ? N(d, "0") + " days" : "—")}</td><td>{E(r.Leaf?.IssuerName)}</td></tr>");
            sb.Append("</tbody></table></div>");
        }
        foreach (var r in reports)
        {
            sb.Append($"<h2 id=\"{E(Anchor(r))}\">{E(r.Host)}:{r.Port}</h2>");
            sb.Append($"<p><b style=\"color:{VerdictColour(r.Verdict)}\">{E(r.Summary)}</b></p>");
            if (r.Error is null)
            {
                sb.Append("<div class=\"stats\">")
                    .Append(Stat("Protocol", r.Protocol, bad: r.Protocol is not ("TLS 1.3" or "TLS 1.2")))
                    .Append(Stat("Cipher", r.CipherSuite))
                    .Append(Stat("ALPN", r.Alpn.Length > 0 ? r.Alpn : "none"))
                    .Append(Stat("Expires in", r.DaysToExpiry is { } d ? $"{N(d, "0")} days" : "—",
                        bad: r.DaysToExpiry is { } dd ? dd <= r.WarnDays : null, hint: r.Leaf is { } l ? l.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null))
                    .Append(Stat("Trusted", r.ChainTrusted ? "yes" : "no", bad: !r.ChainTrusted))
                    .Append(Stat("Hostname", r.HostnameMatches ? "matches" : "mismatch", bad: !r.HostnameMatches))
                    .Append(Stat("Revocation", r.Revocation, bad: r.Revocation == "revoked" ? true : r.Revocation == "good" ? false : null))
                    .Append("</div>");
                sb.Append("<h3>Certificate chain</h3><div class=\"card\"><table><thead><tr><th>#</th><th>Subject</th><th>Issuer</th><th>Valid</th><th>Key</th><th>Signature</th></tr></thead><tbody>");
                for (var i = 0; i < r.Chain.Count; i++)
                {
                    var c = r.Chain[i];
                    sb.Append($"<tr><td>{i + 1}</td><td>{E(c.CommonName)}{(c.IsSelfSigned ? " <span class=\"muted\">(self-signed)</span>" : "")}{(c.IsCertificateAuthority ? " <span class=\"muted\">CA</span>" : "")}</td>" +
                              $"<td>{E(c.IssuerName)}</td><td>{c.NotBefore:yyyy-MM-dd} → {c.NotAfter:yyyy-MM-dd}</td><td>{E(c.PublicKey)}</td><td>{E(c.SignatureAlgorithm)}</td></tr>");
                }
                sb.Append("</tbody></table></div>");
                if (r.Leaf is { SubjectAlternativeNames.Count: > 0 } leaf)
                    sb.Append($"<h3>Subject Alternative Names</h3><p><code>{E(string.Join(", ", leaf.SubjectAlternativeNames))}</code></p>");
                if (r.Leaf is { } lf)
                    sb.Append($"<p class=\"muted\">Serial {E(lf.SerialNumber)} · SHA-1 {E(lf.Thumbprint)}</p>");
            }
            if (r.Insights.Count > 0)
            {
                sb.Append("<ul class=\"ins\">");
                foreach (var insight in r.Insights)
                    sb.Append($"<li class=\"{insight.Css}\">{E(insight.Text)}</li>");
                sb.Append("</ul>");
            }
        }
        sb.Append(End);
        return sb.ToString();
    }

    private static string Anchor(TlsReport r) => $"{r.Host}-{r.Port}".Replace('.', '-');

    private static string VerdictColour(TlsVerdict verdict) => verdict switch
    {
        TlsVerdict.Ok => "var(--ok)",
        TlsVerdict.Warning => "var(--warn)",
        _ => "var(--bad)"
    };

    public static string Json(TlsReport report) => Json([report]);

    public static string Json(IReadOnlyList<TlsReport> reports)
    {
        var root = new JsonObject
        {
            ["passed"] = reports.All(r => r.Passed),
            ["hosts"] = new JsonArray(reports.Select(ToJson).ToArray())
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static JsonNode ToJson(TlsReport r) => new JsonObject
    {
        ["host"] = r.Host,
        ["port"] = r.Port,
        ["serverName"] = r.ServerName,
        ["startedAt"] = r.StartedAt.ToString("O", CultureInfo.InvariantCulture),
        ["durationMs"] = Math.Round(r.Duration.TotalMilliseconds),
        ["handshakeMs"] = r.HandshakeTime is { } h ? Math.Round(h.TotalMilliseconds) : null,
        ["verdict"] = r.Verdict.ToString(),
        ["passed"] = r.Passed,
        ["summary"] = r.Summary,
        ["error"] = r.Error,
        ["protocol"] = r.Protocol,
        ["cipherSuite"] = r.CipherSuite,
        ["alpn"] = r.Alpn,
        ["hostnameMatches"] = r.HostnameMatches,
        ["chainTrusted"] = r.ChainTrusted,
        ["chainErrors"] = new JsonArray(r.ChainErrors.Select(e => (JsonNode)e).ToArray()),
        ["revocation"] = r.Revocation,
        ["daysToExpiry"] = r.DaysToExpiry is { } d ? Math.Round(d, 1) : null,
        ["warnDays"] = r.WarnDays,
        ["chain"] = new JsonArray(r.Chain.Select(c => (JsonNode)new JsonObject
        {
            ["subject"] = c.Subject,
            ["commonName"] = c.CommonName,
            ["issuer"] = c.Issuer,
            ["notBefore"] = c.NotBefore.ToString("O", CultureInfo.InvariantCulture),
            ["notAfter"] = c.NotAfter.ToString("O", CultureInfo.InvariantCulture),
            ["subjectAlternativeNames"] = new JsonArray(c.SubjectAlternativeNames.Select(n => (JsonNode)n).ToArray()),
            ["thumbprint"] = c.Thumbprint,
            ["serialNumber"] = c.SerialNumber,
            ["signatureAlgorithm"] = c.SignatureAlgorithm,
            ["publicKey"] = c.PublicKey,
            ["isCa"] = c.IsCertificateAuthority,
            ["selfSigned"] = c.IsSelfSigned
        }).ToArray()),
        ["insights"] = new JsonArray(r.Insights.Select(i => (JsonNode)new JsonObject { ["level"] = i.Level.ToString(), ["text"] = i.Text }).ToArray())
    };
}
