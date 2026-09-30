using Dispatch.Application.Docs;
using Dispatch.Application.Interop;
using Dispatch.Domain;

namespace Dispatch.Tests;

public class MarkdownTests
{
    [Fact]
    public void Renders_common_blocks()
    {
        var html = Markdown.ToHtml("""
            # Users API
            Returns **all** users, _sorted_ by `name`.
            See [docs](https://example.com/docs "Docs") or https://example.com.

            - one
            - two
              - nested
            1. first
            2. second

            - [x] done
            - [ ] todo

            > note: rate limited

            | Code | Meaning |
            | ---: | :--- |
            | 200 | OK |

            ```json
            {"a": "<b>"}
            ```
            ---
            """);

        Assert.Contains("<h1 id=\"users-api\">Users API</h1>", html);
        Assert.Contains("<p>Returns <strong>all</strong> users, <em>sorted</em> by <code>name</code>. See <a href=\"https://example.com/docs\" title=\"Docs\">docs</a> or <a href=\"https://example.com\">https://example.com</a>.</p>", html);
        Assert.Contains("<ul>\n<li>one</li>\n<li>two\n<ul>\n<li>nested</li>\n</ul>\n</li>\n</ul>", html);
        Assert.Contains("<ol>\n<li>first</li>\n<li>second</li>\n</ol>", html);
        Assert.Contains("<input type=\"checkbox\" checked disabled> done", html);
        Assert.Contains("<blockquote>\n<p>note: rate limited</p>\n</blockquote>", html);
        Assert.Contains("<th style=\"text-align:right\">Code</th>", html);
        Assert.Contains("<td style=\"text-align:left\">OK</td>", html);
        Assert.Contains("<pre><code class=\"language-json\">{&quot;a&quot;: &quot;&lt;b&gt;&quot;}</code></pre>", html);
        Assert.Contains("<hr>", html);
    }

    [Fact]
    public void Raw_html_and_script_urls_are_neutralised()
    {
        var html = Markdown.ToHtml("<script>alert(1)</script> [x](javascript:alert(1))");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("href=\"#\"", html);
    }

    [Fact]
    public void Code_spans_are_not_formatted() =>
        Assert.Equal("<p><code>**not bold** &lt;x&gt;</code></p>", Markdown.ToHtml("`**not bold** <x>`"));
}

public class DocsGeneratorTests
{
    private static RequestCollection Sample() => new()
    {
        Name = "Pet Store",
        Description = "The **pet** API.",
        Variables = [new("baseUrl", "https://api.pets.dev"), new("token", "s3cr3t") { IsSecret = true }],
        Requests =
        [
            new ApiRequest
            {
                Name = "List pets", Folder = "Pets", Method = HttpVerb.Get, Url = "{{baseUrl}}/pets?limit=10",
                QueryParams = [new("limit", "10")], Description = "Paged list.\n\n- sorted by name",
                Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "hardcoded-token" },
                Assertions = [new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" }],
                Examples = [new ResponseExample { Name = "OK", StatusCode = 200, Body = """[{"id":1,"name":"Rex"}]""" }]
            },
            new ApiRequest
            {
                Name = "Create pet", Folder = "Pets", Method = HttpVerb.Post, Url = "{{baseUrl}}/pets",
                Body = new RequestBody { Mode = BodyMode.Json, Content = """{"name":"Rex"}""" }
            },
            new ApiRequest
            {
                Name = "Get pet", Kind = RequestKind.Grpc, Url = "localhost:5001",
                Protocol = new ProtocolSettings { Grpc = new GrpcSettings { Service = "pets.Pets", Method = "Get", Message = """{"id":1}""" } }
            }
        ]
    };

    [Fact]
    public void Html_reference_has_navigation_sections_and_no_secrets()
    {
        var html = DocsGenerator.Html(Sample());
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<title>Pet Store · API reference</title>", html);
        Assert.Contains("<strong>pet</strong> API", html);
        Assert.Contains("<h2 class=\"group\">Pets</h2>", html);
        Assert.Contains("<span class=\"badge get\">GET</span> List pets", html);
        Assert.Contains("<span class=\"var\">{{baseUrl}}</span>/pets?limit=10", html);
        Assert.Contains("<li>sorted by name</li>", html);
        Assert.Contains("Status == 200", html);
        Assert.Contains("&quot;name&quot;: &quot;Rex&quot;", html);
        Assert.Contains("<code>pets.Pets/Get</code>", html);
        Assert.Contains("curl", html);
        Assert.Contains("<em>secret</em>", html);
        Assert.DoesNotContain("s3cr3t", html);
        Assert.DoesNotContain("hardcoded-token", html);
        Assert.Contains("id=\"search\"", html);
    }

    [Fact]
    public void Markdown_reference_lists_endpoints()
    {
        var md = DocsGenerator.MarkdownText(Sample());
        Assert.StartsWith("# Pet Store", md);
        Assert.Contains("| `{{token}}` | _secret_ |", md);
        Assert.Contains("### GET List pets", md);
        Assert.Contains("**Method:** `pets.Pets/Get`", md);
        Assert.Contains("**Response 200** OK", md);
        Assert.DoesNotContain("s3cr3t", md);
    }
}

[Collection("Console")]
public class CliDocsTests
{
    [Fact]
    public async Task Docs_command_writes_html_and_markdown()
    {
        var dir = Cli.TempDir();
        var source = Path.Combine(dir, "pets.dispatch.json");
        await File.WriteAllTextAsync(source, DispatchFormat.ExportCollection(new RequestCollection
        {
            Name = "Pets", Requests = [new ApiRequest { Name = "List", Url = "https://x/pets" }]
        }));

        var html = Path.Combine(dir, "out.html");
        var (exit, output) = await Cli.RunAsync("docs", source, "-o", html, "--db", Path.Combine(dir, "db"));
        Assert.Equal(0, exit);
        Assert.Contains("1 endpoint(s)", output);
        Assert.Contains("<span class=\"badge get\">GET</span> List", await File.ReadAllTextAsync(html));

        var (mdExit, md) = await Cli.RunAsync("docs", source, "--format", "md", "-o", "-", "--db", Path.Combine(dir, "db"));
        Assert.Equal(0, mdExit);
        Assert.Contains("### GET List", md);
    }
}
