using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Dispatch.Domain;

namespace Dispatch.Application.Soap;

public sealed record SoapFault(string Code, string Reason, string? Detail);

/// <summary>SOAP envelope helpers: WS-Security headers and fault parsing.</summary>
public static class SoapEnvelope
{
    public static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    public static readonly XNamespace Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    private const string PasswordText = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText";
    private const string PasswordDigest = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
    private const string Base64Encoding = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    /// <summary>Adds a WS-Security header (UsernameToken and/or Timestamp) to the envelope, replacing any existing one.</summary>
    public static string ApplyWsSecurity(string envelopeXml, SoapSettings settings, DateTimeOffset? now = null, byte[]? nonce = null)
    {
        if (settings.WsSecurity == WsSecurityMode.None && !settings.AddTimestamp)
            return envelopeXml;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(envelopeXml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"The SOAP envelope is not valid XML: {ex.Message}");
        }

        var envelope = doc.Root ?? throw new FormatException("Empty SOAP envelope.");
        var envNs = envelope.Name.Namespace;
        var header = envelope.Element(envNs + "Header");
        if (header is null)
        {
            header = new XElement(envNs + "Header");
            envelope.AddFirst(header);
        }
        header.Elements(Wsse + "Security").Remove();

        var security = new XElement(Wsse + "Security",
            new XAttribute(XNamespace.Xmlns + "wsse", Wsse.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wsu", Wsu.NamespaceName),
            new XAttribute(envNs + "mustUnderstand", envNs == WsdlDocument.Soap12Envelope ? "true" : "1"));

        var created = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var createdText = created.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        if (settings.AddTimestamp)
        {
            security.Add(new XElement(Wsu + "Timestamp",
                new XAttribute(Wsu + "Id", "TS-1"),
                new XElement(Wsu + "Created", createdText),
                new XElement(Wsu + "Expires", created.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))));
        }

        if (settings.WsSecurity != WsSecurityMode.None)
        {
            var token = new XElement(Wsse + "UsernameToken",
                new XAttribute(Wsu + "Id", "UsernameToken-1"),
                new XElement(Wsse + "Username", settings.WsUsername));

            if (settings.WsSecurity == WsSecurityMode.UsernameTokenDigest)
            {
                nonce ??= RandomNumberGenerator.GetBytes(16);
                var digest = SHA1.HashData(nonce.Concat(Encoding.UTF8.GetBytes(createdText + settings.WsPassword)).ToArray());
                token.Add(
                    new XElement(Wsse + "Password", new XAttribute("Type", PasswordDigest), Convert.ToBase64String(digest)),
                    new XElement(Wsse + "Nonce", new XAttribute("EncodingType", Base64Encoding), Convert.ToBase64String(nonce)),
                    new XElement(Wsu + "Created", createdText));
            }
            else
            {
                token.Add(new XElement(Wsse + "Password", new XAttribute("Type", PasswordText), settings.WsPassword));
            }
            security.Add(token);
        }

        header.Add(security);
        return doc.Declaration is null ? doc.Root!.ToString() : doc.Declaration + Environment.NewLine + doc.Root;
    }

    /// <summary>Parses a SOAP 1.1 or 1.2 fault from a response body; null when the body is not a fault.</summary>
    public static SoapFault? ParseFault(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('<'))
            return null;
        XDocument doc;
        try
        {
            doc = XDocument.Parse(body);
        }
        catch (XmlException)
        {
            return null;
        }

        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault"
                                                          && (e.Name.Namespace == WsdlDocument.Soap11Envelope
                                                              || e.Name.Namespace == WsdlDocument.Soap12Envelope));
        if (fault is null)
            return null;

        if (fault.Name.Namespace == WsdlDocument.Soap12Envelope)
        {
            var ns = WsdlDocument.Soap12Envelope;
            var code = fault.Element(ns + "Code")?.Element(ns + "Value")?.Value ?? "";
            var subcode = fault.Element(ns + "Code")?.Element(ns + "Subcode")?.Element(ns + "Value")?.Value;
            var reason = fault.Element(ns + "Reason")?.Elements(ns + "Text").FirstOrDefault()?.Value ?? "";
            return new SoapFault(subcode is null ? code : $"{code} / {subcode}", reason.Trim(),
                fault.Element(ns + "Detail")?.ToString());
        }

        return new SoapFault(
            fault.Element("faultcode")?.Value ?? "",
            (fault.Element("faultstring")?.Value ?? "").Trim(),
            fault.Element("detail")?.ToString());
    }
}
