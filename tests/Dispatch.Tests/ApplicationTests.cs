using System.Text;
using Dispatch.Application.Formatting;
using Dispatch.Application.Requests;
using Dispatch.Application.Variables;
using Dispatch.Domain;

namespace Dispatch.Tests;

public class VariableResolverTests
{
    private static readonly Dictionary<string, string> Vars = new()
    {
        ["host"] = "api.example.com",
        ["baseUrl"] = "https://{{host}}/v1",
        ["a"] = "{{b}}",
        ["b"] = "{{a}}"
    };

    [Theory]
    [InlineData("{{baseUrl}}/users", "https://api.example.com/v1/users")]
    [InlineData("{{ host }}", "api.example.com")]
    [InlineData("no vars", "no vars")]
    [InlineData("{{missing}}/x", "{{missing}}/x")]
    [InlineData("", "")]
    public void Resolves_placeholders(string input, string expected) =>
        Assert.Equal(expected, VariableResolver.Resolve(input, Vars));

    [Fact]
    public void Cyclic_variables_terminate() =>
        Assert.Contains("{{", VariableResolver.Resolve("{{a}}", Vars));

    [Fact]
    public void Reports_unresolved_names() =>
        Assert.Equal(["missing"], VariableResolver.FindUnresolved("{{host}}/{{missing}}", Vars));
}

public class QueryStringTests
{
    [Fact]
    public void Parses_query_keeping_raw_values()
    {
        var items = QueryString.Parse("https://x.io/a?q={{term}}&page=2&flag#top");

        Assert.Collection(items,
            p => { Assert.Equal("q", p.Key); Assert.Equal("{{term}}", p.Value); },
            p => { Assert.Equal("page", p.Key); Assert.Equal("2", p.Value); },
            p => { Assert.Equal("flag", p.Key); Assert.Equal("", p.Value); });
    }

    [Fact]
    public void Rebuilds_url_with_only_enabled_params_and_keeps_fragment()
    {
        var url = QueryString.WithParams("https://x.io/a?old=1#top",
        [
            new KeyValueItem("page", "2"),
            new KeyValueItem("debug", "true", enabled: false),
            new KeyValueItem("flag", "")
        ]);

        Assert.Equal("https://x.io/a?page=2&flag#top", url);
    }

    [Fact]
    public void Removes_question_mark_when_no_params() =>
        Assert.Equal("https://x.io/a", QueryString.WithParams("https://x.io/a?x=1", []));

    [Fact]
    public void ParseForm_decodes_plus_and_percent_escapes()
    {
        var fields = QueryString.ParseForm("user=ann%40x.io&note=a+b%26c&flag");

        Assert.Equal(["user", "note", "flag"], fields.Select(f => f.Key));
        Assert.Equal("ann@x.io", fields[0].Value);
        Assert.Equal("a b&c", fields[1].Value);
        Assert.Equal("", fields[2].Value);
        Assert.Empty(QueryString.ParseForm(" "));
    }
}

public class RequestMessageBuilderTests
{
    private readonly RequestMessageBuilder _builder = new();
    private static readonly Dictionary<string, string> Vars = new() { ["base"] = "https://api.test", ["tok"] = "abc" };

    [Fact]
    public void Builds_url_from_params_table_excluding_disabled_rows()
    {
        var request = new ApiRequest
        {
            Url = "{{base}}/items?page=1&debug=1",
            QueryParams = [new("page", "1"), new("debug", "1", enabled: false), new("q", "a b")]
        };

        using var message = _builder.Build(request, Vars);

        Assert.Equal("https://api.test/items?page=1&q=a%20b", message.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void Encodes_param_values_that_would_break_the_query_but_keeps_existing_escapes()
    {
        var request = new ApiRequest
        {
            Url = "{{base}}/search",
            QueryParams =
            [
                new("lang", "C#"),           // '#' would start a fragment
                new("q", "a&b=c"),           // '&' and '=' would split into extra params
                new("phone", "+1 555"),      // '+' would be read as a space by the server
                new("pre", "x%20y"),         // already encoded: must not become x%2520y
                new("city", "Zürich"),       // non-ASCII
                new("pct", "100%")           // bare '%' is not an escape
            ]
        };

        using var message = _builder.Build(request, Vars);

        Assert.Equal(
            "https://api.test/search?lang=C%23&q=a%26b%3Dc&phone=%2B1%20555&pre=x%20y&city=Z%C3%BCrich&pct=100%25",
            message.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void Rejects_unresolved_variables_in_params_table()
    {
        var request = new ApiRequest { Url = "{{base}}/items", QueryParams = [new("token", "{{missing}}"), new("off", "{{alsoMissing}}", enabled: false)] };

        var error = Assert.Throws<RequestBuildException>(() => _builder.Build(request, Vars));

        Assert.Contains("{{missing}}", error.Message);
        Assert.DoesNotContain("alsoMissing", error.Message); // disabled rows are ignored
    }

    [Fact]
    public void Defaults_to_http_scheme() =>
        Assert.Equal("http", _builder.Build(new ApiRequest { Url = "localhost:5000/health" }, Vars).RequestUri!.Scheme);

    [Theory]
    [InlineData("")]
    [InlineData("ftp://files.test/x")]
    [InlineData("{{nope}}/x")]
    public void Rejects_invalid_urls(string url) =>
        Assert.Throws<RequestBuildException>(() => _builder.Build(new ApiRequest { Url = url }, Vars));

    [Fact]
    public async Task Json_body_gets_content_type_and_resolved_variables()
    {
        var request = new ApiRequest
        {
            Method = HttpVerb.Post,
            Url = "{{base}}/x",
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"token":"{{tok}}"}""" }
        };

        using var message = _builder.Build(request, Vars);

        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""{"token":"abc"}""", await message.Content.ReadAsStringAsync());
    }

    [Fact]
    public void User_content_type_header_overrides_default()
    {
        var request = new ApiRequest
        {
            Method = HttpVerb.Post,
            Url = "{{base}}/x",
            Headers = [new("Content-Type", "application/vnd.api+json"), new("X-Trace", "1")],
            Body = new RequestBody { Mode = BodyMode.Json, Content = "{}" }
        };

        using var message = _builder.Build(request, Vars);

        Assert.Equal("application/vnd.api+json", message.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("1", message.Headers.GetValues("X-Trace").Single());
    }

    [Fact]
    public void Applies_bearer_auth_with_variables()
    {
        var request = new ApiRequest { Url = "{{base}}", Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "{{tok}}" } };

        using var message = _builder.Build(request, Vars);

        Assert.Equal("Bearer abc", message.Headers.Authorization!.ToString());
    }

    [Fact]
    public void Applies_basic_auth()
    {
        var request = new ApiRequest
        {
            Url = "{{base}}",
            Auth = new AuthSettings { Mode = AuthMode.Basic, Username = "user", Password = "pa:ss" }
        };

        using var message = _builder.Build(request, Vars);

        Assert.Equal("Basic", message.Headers.Authorization!.Scheme);
        Assert.Equal("user:pa:ss", Encoding.UTF8.GetString(Convert.FromBase64String(message.Headers.Authorization.Parameter!)));
    }

    [Fact]
    public void Api_key_can_go_to_query_string()
    {
        var request = new ApiRequest
        {
            Url = "{{base}}/x?a=1",
            QueryParams = [new("a", "1")],
            Auth = new AuthSettings { Mode = AuthMode.ApiKey, ApiKeyName = "api_key", ApiKeyValue = "s&cret", ApiKeyLocation = ApiKeyLocation.QueryParam }
        };

        using var message = _builder.Build(request, Vars);

        Assert.Equal("https://api.test/x?a=1&api_key=s%26cret", message.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Form_urlencoded_body_skips_disabled_fields()
    {
        var request = new ApiRequest
        {
            Method = HttpVerb.Post,
            Url = "{{base}}",
            Body = new RequestBody
            {
                Mode = BodyMode.FormUrlEncoded,
                FormFields = [new("name", "Hovo Test"), new("skip", "1", enabled: false)]
            }
        };

        using var message = _builder.Build(request, Vars);

        Assert.Equal("name=Hovo+Test", await message.Content!.ReadAsStringAsync());
    }

    [Fact]
    public void Adds_default_user_agent_and_accept()
    {
        using var message = _builder.Build(new ApiRequest { Url = "{{base}}" }, Vars);

        Assert.Equal(RequestMessageBuilder.DefaultUserAgent, message.Headers.UserAgent.ToString());
        Assert.Equal("*/*", message.Headers.Accept.ToString());
    }
}

public class BodyFormatterTests
{
    [Fact]
    public void Pretty_prints_json_and_keeps_unicode()
    {
        var pretty = BodyFormatter.Pretty("""{"city":"Երևան","n":[1,2]}""", "application/json");

        Assert.Contains("\"city\": \"Երևան\"", pretty);
        Assert.Contains(Environment.NewLine, pretty);
    }

    [Fact]
    public void Pretty_prints_xml() =>
        Assert.Contains(Environment.NewLine, BodyFormatter.Pretty("<a><b>1</b></a>", "application/xml"));

    [Fact]
    public void Leaves_plain_text_untouched() =>
        Assert.Equal("hello {", BodyFormatter.Pretty("hello {", "text/plain"));
}

public class BodyFormatDetectionTests
{
    [Theory]
    [InlineData("application/json", BodyFormat.Json)]
    [InlineData("application/problem+json; charset=utf-8", BodyFormat.Json)]
    [InlineData("text/html", BodyFormat.Html)]
    [InlineData("application/xhtml+xml", BodyFormat.Html)]
    [InlineData("application/soap+xml", BodyFormat.Xml)]
    [InlineData("text/javascript", BodyFormat.JavaScript)]
    [InlineData("application/x-www-form-urlencoded", BodyFormat.Form)]
    [InlineData("text/css", BodyFormat.Text)]
    public void Detects_format_from_content_type(string contentType, BodyFormat expected) =>
        Assert.Equal(expected, BodyFormatter.Detect(contentType, "{}"));

    [Theory]
    [InlineData("""{"a":1}""", BodyFormat.Json)]
    [InlineData("<!DOCTYPE html><html></html>", BodyFormat.Html)]
    [InlineData("<a><b/></a>", BodyFormat.Xml)]
    [InlineData("hello {", BodyFormat.Text)]
    public void Sniffs_body_when_content_type_is_missing_or_generic(string body, BodyFormat expected)
    {
        Assert.Equal(expected, BodyFormatter.Detect(null, body));
        Assert.Equal(expected, BodyFormatter.Detect("text/plain", body));
    }

    [Fact]
    public void Json_sent_as_html_is_not_reformatted_as_json() =>
        Assert.Equal("""{"a":1}""", BodyFormatter.Pretty("""{"a":1}""", "text/html"));

    [Fact]
    public void Indents_html_and_keeps_leaf_elements_on_one_line()
    {
        var pretty = BodyFormatter.Pretty(
            "<!DOCTYPE html><html><head><title>Home</title><meta charset=\"utf-8\"></head>" +
            "<body><ul><li>One<li>Two</ul><p>a &lt; b</p></body></html>", "text/html");

        var expected = string.Join(Environment.NewLine,
            "<!DOCTYPE html>",
            "<html>",
            "  <head>",
            "    <title>Home</title>",
            "    <meta charset=\"utf-8\">",
            "  </head>",
            "  <body>",
            "    <ul>",
            "      <li>",
            "        One",
            "      <li>",
            "        Two",
            "    </ul>",
            "    <p>a &lt; b</p>",
            "  </body>",
            "</html>");
        Assert.Equal(expected, pretty);
    }

    [Fact]
    public void Keeps_script_content_verbatim()
    {
        var pretty = BodyFormatter.FormatHtml("<div><script>if (a < b) {\n  go();\n}</script></div>");

        Assert.Contains("if (a < b) {\n  go();\n}", pretty);
        Assert.EndsWith("</div>", pretty);
    }

    [Fact]
    public void Decodes_form_bodies() =>
        Assert.Equal($"name: Hovo Test{Environment.NewLine}city: Երևան",
            BodyFormatter.Pretty("name=Hovo+Test&city=%D4%B5%D6%80%D6%87%D5%A1%D5%B6", "application/x-www-form-urlencoded"));
}

public class KeyValueBulkTextTests
{
    [Fact]
    public void Round_trips_rows_including_disabled_ones()
    {
        var items = new List<KeyValueItem>
        {
            new("Content-Type", "application/json"),
            new("X-Debug", "1", enabled: false),
            new("redirect", "https://a.test/x?y=1")
        };

        var text = KeyValueBulkText.Format(items);
        var parsed = KeyValueBulkText.Parse(text);

        Assert.Equal("Content-Type: application/json" + Environment.NewLine + "// X-Debug: 1"
                     + Environment.NewLine + "redirect: https://a.test/x?y=1", text);
        Assert.Equal(items.Select(i => (i.Key, i.Value, i.Enabled)), parsed.Select(i => (i.Key, i.Value, i.Enabled)));
    }

    [Fact]
    public void Parses_loose_input()
    {
        var parsed = KeyValueBulkText.Parse("Accept:*/*\r\n\r\n  //Authorization :  Bearer x \nflag\n");

        Assert.Collection(parsed,
            a => Assert.Equal(("Accept", "*/*", true), (a.Key, a.Value, a.Enabled)),
            b => Assert.Equal(("Authorization", "Bearer x", false), (b.Key, b.Value, b.Enabled)),
            c => Assert.Equal(("flag", "", true), (c.Key, c.Value, c.Enabled)));
    }
}

public class DefaultHeadersTests
{
    [Fact]
    public void New_requests_get_editable_copies_of_the_defaults()
    {
        var first = DefaultHeaders.Create();
        first[0].Value = "changed";

        Assert.Equal("*/*", DefaultHeaders.Create()[0].Value);
        Assert.Contains(first, h => h.Key == "User-Agent" && h.Value == RequestMessageBuilder.DefaultUserAgent);
    }

    [Fact]
    public void Default_headers_are_sent()
    {
        using var message = new RequestMessageBuilder().Build(
            new ApiRequest { Url = "https://a.test", Headers = DefaultHeaders.Create() }, new Dictionary<string, string>());

        Assert.Equal("no-cache", message.Headers.CacheControl?.ToString());
        Assert.Contains("gzip", message.Headers.AcceptEncoding.ToString());
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("", true)]
    [InlineData("application/vnd.api+json", false)]
    public void Recognises_generated_content_types(string value, bool expected) =>
        Assert.Equal(expected, DefaultHeaders.IsGeneratedContentType(value));
}
