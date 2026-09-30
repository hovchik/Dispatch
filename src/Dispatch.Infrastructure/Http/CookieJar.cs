using System.Net;
using System.Text.Json;
using Dispatch.Application.Abstractions;

namespace Dispatch.Infrastructure.Http;

/// <summary>An RFC 6265 cookie store (via <see cref="CookieContainer"/>) that can be saved to and loaded from JSON.</summary>
public sealed class CookieJar : ICookieJar
{
    private readonly Lock _gate = new();
    private CookieContainer _container = new();

    public event EventHandler? Changed;

    public string? GetCookieHeader(Uri uri)
    {
        lock (_gate)
        {
            var header = _container.GetCookieHeader(uri);
            return header.Length == 0 ? null : header;
        }
    }

    public void Store(Uri uri, IEnumerable<string> setCookieHeaders)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var header in setCookieHeaders)
            {
                try
                {
                    _container.SetCookies(uri, header);
                    changed = true;
                }
                catch (CookieException)
                {
                    // Malformed or cross-domain cookies are ignored, like browsers do.
                }
            }
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<CookieInfo> GetAll()
    {
        lock (_gate)
        {
            return _container.GetAllCookies()
                .Where(c => !c.Expired)
                .Select(c => new CookieInfo(c.Domain, c.Path, c.Name, c.Value,
                    c.Expires == DateTime.MinValue ? null : new DateTimeOffset(c.Expires.ToUniversalTime()),
                    c.Secure, c.HttpOnly))
                .OrderBy(c => c.Domain).ThenBy(c => c.Name)
                .ToList();
        }
    }

    public void Delete(string domain, string path, string name)
    {
        lock (_gate)
        {
            foreach (var cookie in _container.GetAllCookies().Where(c => c.Domain == domain && c.Path == path && c.Name == name))
                cookie.Expired = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
            _container = new CookieContainer();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record StoredCookie(string Domain, string Path, string Name, string Value, DateTime? Expires,
        bool Secure, bool HttpOnly);

    public string Export()
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(_container.GetAllCookies()
                .Where(c => !c.Expired)
                .Select(c => new StoredCookie(c.Domain, c.Path, c.Name, c.Value,
                    c.Expires == DateTime.MinValue ? null : c.Expires.ToUniversalTime(), c.Secure, c.HttpOnly)));
        }
    }

    public void Import(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        List<StoredCookie>? cookies;
        try
        {
            cookies = JsonSerializer.Deserialize<List<StoredCookie>>(json);
        }
        catch (JsonException)
        {
            return;
        }
        lock (_gate)
        {
            foreach (var c in cookies ?? [])
            {
                try
                {
                    var cookie = new Cookie(c.Name, c.Value, c.Path, c.Domain) { Secure = c.Secure, HttpOnly = c.HttpOnly };
                    if (c.Expires is { } expires)
                        cookie.Expires = expires;
                    _container.Add(cookie);
                }
                catch (CookieException)
                {
                }
            }
        }
    }
}
