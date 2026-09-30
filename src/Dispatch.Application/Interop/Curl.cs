using System.Text;
using Dispatch.Application.Requests;
using Dispatch.Domain;

namespace Dispatch.Application.Interop;

/// <summary>Parses <c>curl</c> command lines (as copied from browser dev tools or docs) into requests.</summary>
public static class Curl
{
    public static bool LooksLikeCurl(string text) => text.TrimStart().StartsWith("curl ", StringComparison.OrdinalIgnoreCase)
                                                     || text.TrimStart().StartsWith("curl.exe ", StringComparison.OrdinalIgnoreCase);

    public static ApiRequest Parse(string command)
    {
        var args = Tokenize(command);
        if (args.Count == 0 || !args[0].StartsWith("curl", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Not a curl command.");

        var request = new ApiRequest { Name = "Imported from cURL" };
        string? method = null;
        var data = new List<string>();
        var urlEncoded = new List<string>();
        var form = new List<KeyValueItem>();
        var getWithData = false;
        string? url = null;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new FormatException($"Missing value after {arg}.");

            // --opt=value form
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('=') && !arg.StartsWith("--data", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=');
                args.Insert(i + 1, arg[(eq + 1)..]);
                arg = arg[..eq];
            }

            switch (arg)
            {
                case "-X" or "--request":
                    method = Next();
                    break;
                case "-H" or "--header":
                    var header = Next();
                    var colon = header.IndexOf(':');
                    if (colon > 0)
                        request.Headers.Add(new KeyValueItem(header[..colon].Trim(), header[(colon + 1)..].Trim()));
                    break;
                case "-d" or "--data" or "--data-ascii" or "--data-raw" or "--data-binary":
                    var value = Next();
                    data.Add(arg != "--data-raw" && value.StartsWith('@') ? ReadFileArgument(value) : value);
                    break;
                case "--json":
                    data.Add(Next());
                    request.Headers.Add(new KeyValueItem("Content-Type", "application/json"));
                    request.Headers.Add(new KeyValueItem("Accept", "application/json"));
                    break;
                case "--data-urlencode":
                    urlEncoded.Add(Next());
                    break;
                case "-F" or "--form" or "--form-string":
                    var field = Next();
                    var eqIndex = field.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        var fieldValue = field[(eqIndex + 1)..];
                        var isFile = arg != "--form-string" && fieldValue.StartsWith('@');
                        form.Add(new KeyValueItem(field[..eqIndex], isFile ? fieldValue[1..].Split(';')[0] : fieldValue) { IsFile = isFile });
                    }
                    break;
                case "-u" or "--user":
                    var user = Next();
                    var sep = user.IndexOf(':');
                    request.Auth = new AuthSettings
                    {
                        Mode = AuthMode.Basic,
                        Username = sep < 0 ? user : user[..sep],
                        Password = sep < 0 ? "" : user[(sep + 1)..]
                    };
                    break;
                case "--digest":
                    request.Auth.Mode = AuthMode.Digest;
                    break;
                case "--ntlm":
                    request.Auth.Mode = AuthMode.Ntlm;
                    break;
                case "-A" or "--user-agent":
                    request.Headers.Add(new KeyValueItem("User-Agent", Next()));
                    break;
                case "-e" or "--referer":
                    request.Headers.Add(new KeyValueItem("Referer", Next()));
                    break;
                case "-b" or "--cookie":
                    request.Headers.Add(new KeyValueItem("Cookie", Next()));
                    break;
                case "-G" or "--get":
                    getWithData = true;
                    break;
                case "-I" or "--head":
                    method = "HEAD";
                    break;
                case "-k" or "--insecure":
                    request.Settings.VerifySsl = false;
                    break;
                case "-L" or "--location":
                    request.Settings.FollowRedirects = true;
                    break;
                case "--max-redirs":
                    request.Settings.MaxRedirects = int.TryParse(Next(), out var redirects) ? redirects : 10;
                    break;
                case "-m" or "--max-time":
                    request.Settings.TimeoutMs = double.TryParse(Next(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? (int)(seconds * 1000) : 0;
                    break;
                case "-x" or "--proxy":
                    request.Settings.Proxy = Next();
                    break;
                case "-E" or "--cert":
                    var cert = Next();
                    var certColon = cert.LastIndexOf(':');
                    if (certColon > 1) // keep Windows drive letters
                    {
                        request.Settings.ClientCertificatePath = cert[..certColon];
                        request.Settings.ClientCertificatePassword = cert[(certColon + 1)..];
                    }
                    else
                    {
                        request.Settings.ClientCertificatePath = cert;
                    }
                    break;
                case "--key":
                    request.Settings.ClientCertificateKeyPath = Next();
                    break;
                case "--http1.1":
                    request.Settings.HttpVersion = HttpVersionPreference.Http11;
                    break;
                case "--http2" or "--http2-prior-knowledge":
                    request.Settings.HttpVersion = HttpVersionPreference.Http2;
                    break;
                case "--http3":
                    request.Settings.HttpVersion = HttpVersionPreference.Http3;
                    break;
                case "--url":
                    url = Next();
                    break;
                case "--oauth2-bearer":
                    request.Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = Next() };
                    break;
                case "--compressed" or "-s" or "--silent" or "-S" or "--show-error" or "-v" or "--verbose" or "-i" or "--include"
                    or "-f" or "--fail" or "--no-progress-meter" or "-N" or "--no-buffer":
                    break;
                case "-o" or "--output" or "-w" or "--write-out" or "--connect-timeout" or "--retry" or "-c" or "--cookie-jar"
                    or "--cacert" or "--capath" or "--resolve" or "--interface":
                    Next(); // options with a value that don't affect the request definition
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                        break; // unknown flag
                    url ??= arg;
                    break;
            }
        }

        if (url is null)
            throw new FormatException("The curl command has no URL.");

        if (getWithData && (data.Count > 0 || urlEncoded.Count > 0))
        {
            var query = string.Join("&", data.Concat(urlEncoded.Select(EncodeDataUrlEncode)));
            url += (url.Contains('?') ? "&" : "?") + query;
            data.Clear();
            urlEncoded.Clear();
        }

        request.Url = url;
        request.QueryParams = QueryString.Parse(url).ToList();

        if (form.Count > 0)
        {
            request.Body = new RequestBody { Mode = BodyMode.Multipart, FormFields = form };
        }
        else if (data.Count > 0 || urlEncoded.Count > 0)
        {
            var body = string.Join("&", data.Concat(urlEncoded.Select(EncodeDataUrlEncode)));
            var contentType = request.Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            var trimmed = body.TrimStart();
            if (contentType.Contains("json") || (contentType.Length == 0 && (trimmed.StartsWith('{') || trimmed.StartsWith('['))))
                request.Body = new RequestBody { Mode = BodyMode.Json, Content = body };
            else if (contentType.Contains("xml") || (contentType.Length == 0 && trimmed.StartsWith('<')))
                request.Body = new RequestBody { Mode = BodyMode.Xml, Content = body };
            else if (contentType.Length == 0 || contentType.Contains("x-www-form-urlencoded"))
                request.Body = new RequestBody
                {
                    Mode = BodyMode.FormUrlEncoded,
                    FormFields = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
                    {
                        var eq = pair.IndexOf('=');
                        return eq < 0
                            ? new KeyValueItem(Uri.UnescapeDataString(pair), "")
                            : new KeyValueItem(Uri.UnescapeDataString(pair[..eq].Replace('+', ' ')), Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')));
                    }).ToList()
                };
            else
                request.Body = new RequestBody { Mode = BodyMode.Text, Content = body };
        }

        method ??= request.Body.Mode == BodyMode.None ? "GET" : "POST";
        request.Method = Enum.TryParse<HttpVerb>(method, ignoreCase: true, out var verb) ? verb : HttpVerb.Get;
        request.Name = $"{request.Method.ToString().ToUpperInvariant()} {TrimUrl(url)}";
        return request;
    }

    private static string TrimUrl(string url)
    {
        var withoutQuery = url.Split('?')[0];
        var schemeEnd = withoutQuery.IndexOf("://", StringComparison.Ordinal);
        var path = schemeEnd < 0 ? withoutQuery : withoutQuery[(withoutQuery.IndexOf('/', schemeEnd + 3) is var p and >= 0 ? p : withoutQuery.Length)..];
        return path.Length == 0 ? withoutQuery : path;
    }

    private static string EncodeDataUrlEncode(string value)
    {
        var eq = value.IndexOf('=');
        return eq < 0 ? Uri.EscapeDataString(value) : value[..eq] + "=" + Uri.EscapeDataString(value[(eq + 1)..]);
    }

    private static string ReadFileArgument(string value)
    {
        var path = value[1..];
        return File.Exists(path) ? File.ReadAllText(path) : value;
    }

    /// <summary>Splits a shell command line: POSIX quoting, backslash / caret / backtick line continuations.</summary>
    internal static List<string> Tokenize(string command)
    {
        var text = command.Replace("\\\r\n", " ").Replace("\\\n", " ").Replace("^\r\n", " ").Replace("^\n", " ")
            .Replace("`\r\n", " ").Replace("`\n", " ");
        var args = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                inToken = true;
                var end = text.IndexOf('\'', i + 1);
                if (end < 0) end = text.Length;
                current.Append(text, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '$' && i + 1 < text.Length && text[i + 1] == '\'')
            {
                // Bash ANSI-C quoting: $'...' with \n, \t, \', \\ escapes.
                inToken = true;
                i += 2;
                while (i < text.Length && text[i] != '\'')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        current.Append(text[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => text[i] });
                    }
                    else
                    {
                        current.Append(text[i]);
                    }
                    i++;
                }
            }
            else if (c == '"')
            {
                inToken = true;
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`')
                        i++;
                    current.Append(text[i]);
                    i++;
                }
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                inToken = true;
                current.Append(text[++i]);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                inToken = true;
                current.Append(c);
            }
        }
        if (inToken)
            args.Add(current.ToString());
        return args;
    }
}
