using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dispatch.Infrastructure.Capture;

/// <summary>
/// A local certificate authority for HTTPS interception: a self-signed CA whose private key stays on this machine, and
/// per-host leaf certificates minted on demand and signed by it. To decrypt HTTPS the user installs and trusts the CA
/// certificate (export it from the proxy tool). Leaf certs are cached per host for the life of the proxy.
/// </summary>
public sealed class CertificateAuthority : IDisposable
{
    private readonly X509Certificate2 _ca;
    private readonly ConcurrentDictionary<string, X509Certificate2> _leaves = new(StringComparer.OrdinalIgnoreCase);

    public CertificateAuthority(X509Certificate2? ca = null) => _ca = ca ?? Create();

    /// <summary>The CA certificate (public part only) the user must trust, PEM-encoded.</summary>
    public string CaCertificatePem => _ca.ExportCertificatePem();

    public X509Certificate2 CaCertificate => _ca;

    /// <summary>Creates a self-signed CA valid for 5 years.</summary>
    public static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Dispatch Capture CA, O=Dispatch", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        var now = DateTimeOffset.UtcNow;
        var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
        // Re-import so the key is persisted with the cert (needed to sign leaves).
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>A leaf certificate for <paramref name="host"/>, signed by the CA, cached per host.</summary>
    public X509Certificate2 GetCertificate(string host) => _leaves.GetOrAdd(host, Mint);

    private X509Certificate2 Mint(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        if (System.Net.IPAddress.TryParse(host, out var ip))
            san.AddIpAddress(ip);
        else
            san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var leaf = request.Create(_ca, now.AddDays(-1), now.AddYears(1), serial);
        // Attach the private key and re-export so SslStream can use it as a server cert.
        using var withKey = leaf.CopyWithPrivateKey(rsa);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    public void Dispose()
    {
        _ca.Dispose();
        foreach (var leaf in _leaves.Values)
            leaf.Dispose();
    }
}
