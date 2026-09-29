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
