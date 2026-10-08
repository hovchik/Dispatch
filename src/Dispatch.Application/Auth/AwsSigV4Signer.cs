using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dispatch.Domain;

namespace Dispatch.Application.Auth;

/// <summary>Signs an HTTP request with AWS Signature Version 4 (header-based).</summary>
public static class AwsSigV4Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    public static async Task SignAsync(HttpRequestMessage request, AuthSettings auth, DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(auth.AwsAccessKey) || string.IsNullOrWhiteSpace(auth.AwsSecretKey))
            throw new InvalidOperationException("AWS access key and secret key are required.");

        var time = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var amzDate = time.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = time.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var payloadHash = Hex(SHA256.HashData(body));

        request.Headers.Remove("X-Amz-Date");
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        if (auth.AwsService == "s3")
        {
            request.Headers.Remove("X-Amz-Content-Sha256");
            request.Headers.TryAddWithoutValidation("X-Amz-Content-Sha256", payloadHash);
        }
        if (!string.IsNullOrWhiteSpace(auth.AwsSessionToken))
        {
            request.Headers.Remove("X-Amz-Security-Token");
            request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", auth.AwsSessionToken);
        }

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"
        };
        foreach (var h in request.Headers)
        {
            var name = h.Key.ToLowerInvariant();
            if (name is "host" or "user-agent" or "accept" or "connection" or "accept-encoding" or "cache-control")
                continue;
            headers[name] = string.Join(",", h.Value.Select(v => v.Trim()));
        }
        if (request.Content?.Headers.ContentType is { } contentType)
            headers["content-type"] = contentType.ToString();

        var signedHeaders = string.Join(";", headers.Keys);
        var canonicalRequest = string.Join("\n",
            request.Method.Method.ToUpperInvariant(),
            CanonicalPath(uri, auth.AwsService),
            CanonicalQuery(uri),
            string.Concat(headers.Select(h => $"{h.Key}:{h.Value}\n")),
            signedHeaders,
            payloadHash);

        var scope = $"{dateStamp}/{auth.AwsRegion}/{auth.AwsService}/aws4_request";
        var stringToSign = string.Join("\n", Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + auth.AwsSecretKey), dateStamp);
        key = Hmac(key, auth.AwsRegion);
        key = Hmac(key, auth.AwsService);
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));

        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization",
            $"{Algorithm} Credential={auth.AwsAccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    internal static string CanonicalPath(Uri uri, string service)
    {
        var path = uri.AbsolutePath.Length == 0 ? "/" : uri.AbsolutePath;
        // S3 uses the path as-is; every other service double-encodes each segment: AbsolutePath is already
        // URI-encoded once, and SigV4 encodes those segments a second time (botocore: quote(path, safe='/~')).
        if (service == "s3")
            return path;
        return string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
    }

    private static string CanonicalQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
            return string.Empty;
        return string.Join("&", query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                var k = eq < 0 ? pair : pair[..eq];
                var v = eq < 0 ? "" : pair[(eq + 1)..];
                return (Key: Uri.EscapeDataString(Uri.UnescapeDataString(k.Replace('+', ' '))),
                    Value: Uri.EscapeDataString(Uri.UnescapeDataString(v.Replace('+', ' '))));
            })
            .OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
