using System.Text.Json.Nodes;
using Dispatch.Application.Formatting;
using Dispatch.Application.Requests;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Dispatch.Infrastructure.Scripting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Tests;

public class JsonTableTests
{
    [Fact]
    public void Root_array_of_objects_becomes_rows_with_flattened_columns()
    {
        var table = JsonTable.Build("""[{"id":1,"user":{"name":"Ann","geo":{"city":"Paris"}},"tags":["a","b"]},{"id":2,"extra":true}]""")!;
        Assert.Equal(["id", "user.name", "user.geo.city", "tags", "extra"], table.Columns);
        Assert.Equal(["1", "Ann", "Paris", """["a","b"]""", ""], table.Rows[0]);
        Assert.Equal(["2", "", "", "", "true"], table.Rows[1]);
        Assert.Equal("$", table.Source);
    }

    [Fact]
    public void Finds_the_largest_array_inside_an_envelope_or_uses_a_path()
    {
        var body = """{"meta":{"page":1},"errors":[{"x":1}],"data":[{"n":"a"},{"n":"b"},{"n":"c"}]}""";
        var table = JsonTable.Build(body)!;
        Assert.Equal("$.data", table.Source);
        Assert.Equal(3, table.Rows.Count);

        var byPath = JsonTable.Build(body, "$.data[*].n")!;
        Assert.Equal(["value"], byPath.Columns);
        Assert.Equal(["a", "b", "c"], byPath.Rows.Select(r => r[0]));
    }

    [Fact]
    public void Non_tabular_bodies_give_null() =>
        Assert.All(new[] { "", "hello", """{"a":1}""", "[]", "{not json" }, b => Assert.Null(JsonTable.Build(b)));

    [Fact]
    public void Csv_escapes_commas_quotes_and_newlines()
    {
        var csv = JsonTable.Build("""[{"a":"x,y","b":"say \"hi\"","c":"1\n2"}]""")!.ToCsv();
        Assert.Equal("a,b,c\n\"x,y\",\"say \"\"hi\"\"\",\"1\n2\"\n", csv.Replace("\r\n", "\n"));
    }
}

public class TemplateTests
{
    [Fact]
    public void Renders_values_loops_conditions_and_escapes_html()
    {
        var data = JsonNode.Parse("""{"title":"<Users>","users":[{"name":"Ann","admin":true},{"name":"Bob","admin":false}],"empty":[]}""");
        var html = Template.Render("""
            <h1>{{title}}</h1>{{{title}}}
            {{#each users}}<li>{{@index}}:{{name}}{{#if admin}} (admin){{else}} (user){{/if}} of {{../title}}</li>{{/each}}
            {{#each empty}}x{{else}}none{{/each}}{{#unless empty}}!{{/unless}}{{! a comment }}
            {{users.length}} {{users.1.name}}
            """, data);
        Assert.Contains("<h1>&lt;Users&gt;</h1><Users>", html);
        Assert.Contains("<li>0:Ann (admin) of &lt;Users&gt;</li><li>1:Bob (user) of &lt;Users&gt;</li>", html);
        Assert.Contains("none!", html);
        Assert.Contains("2 Bob", html);
        Assert.DoesNotContain("comment", html);
    }

    [Fact]
    public void Nested_blocks_and_with()
    {
        var data = JsonNode.Parse("""{"groups":[{"name":"g1","items":[1,2]},{"name":"g2","items":[]}],"owner":{"email":"a@b.c"}}""");
        var html = Template.Render("{{#each groups}}[{{name}}:{{#each items}}{{this}};{{else}}-{{/each}}]{{/each}}{{#with owner}}{{email}}{{/with}}", data);
        Assert.Equal("[g1:1;2;][g2:-]a@b.c", html);
    }

    [Fact]
    public async Task Test_scripts_can_set_a_visualization()
    {
        var response = new ApiResponse { StatusCode = 200, Body = """{"items":[{"n":"a"},{"n":"b"}]}""" };
        var result = await new JintScriptRunner().RunTestsAsync("""
            pm.visualizer.set('<table>{{#each items}}<tr><td>{{n}}</td></tr>{{/each}}</table>', pm.response.json());
            """, new ApiRequest { Name = "List" }, response, new(), CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Contains("<tr><td>a</td></tr><tr><td>b</td></tr>", result.Visualization);
        Assert.Contains("<title>List</title>", result.Visualization);
    }
}

public class BodyPreviewTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Theory]
    [InlineData("image/png", PreviewKind.Image)]
    [InlineData("application/octet-stream", PreviewKind.Image)] // sniffed
    [InlineData("image/svg+xml", PreviewKind.Svg)]
    [InlineData("application/pdf", PreviewKind.Pdf)]
    public void Detects_previewable_bodies(string contentType, PreviewKind expected) =>
        Assert.Equal(expected, BodyPreview.Detect(contentType, contentType == "image/svg+xml" ? null : Png, "<svg/>"));

    [Fact]
    public void Html_and_extensions()
    {
        Assert.Equal(PreviewKind.Html, BodyPreview.Detect("text/html; charset=utf-8", null, "<p>x</p>"));
        Assert.Equal(PreviewKind.None, BodyPreview.Detect("application/json", null, "{}"));
        Assert.Equal(".png", BodyPreview.Extension("image/png", BodyFormat.Text));
        Assert.Equal(".json", BodyPreview.Extension("application/problem+json", BodyFormat.Json));
        Assert.Equal("Title\nHello world\n• one\n• two",
            BodyPreview.HtmlToText("<html><head><style>x{}</style></head><body><h1>Title</h1><p>Hello   <b>world</b></p><ul><li>one</li><li>two</li></ul><script>x()</script></body></html>"));
    }

    [Fact]
    public async Task Binary_http_bodies_keep_their_bytes()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/img", () => Results.Bytes(Png, "image/png"));
            app.MapGet("/json", () => Results.Json(new { a = 1 }));
        });
        await using var services = new ServiceCollection().AddDispatchEngine().AddSingleton<Application.Abstractions.IHistoryRepository, NullHistory>()
            .BuildServiceProvider();
        var sender = services.GetRequiredService<IRequestSender>();

        var image = await sender.SendAsync(new ApiRequest { Url = server.BaseUrl + "/img" }, new SendOptions { RecordHistory = false }, CancellationToken.None);
        Assert.Equal(Png, image.BodyBytes);
        var json = await sender.SendAsync(new ApiRequest { Url = server.BaseUrl + "/json" }, new SendOptions { RecordHistory = false }, CancellationToken.None);
        Assert.Null(json.BodyBytes);
    }
}
