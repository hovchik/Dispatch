using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dispatch.Application.Interop;
using Dispatch.Application.Mock;
using Dispatch.Application.Testing;
using Dispatch.Application.Variables;
using Dispatch.Domain;
using Dispatch.Infrastructure.Mock;

namespace Dispatch.Tests;

public class SchemaFakerTests
{
    private const string PetSchema = """
        {
          "type": "object",
          "required": ["id", "name", "status", "tags", "owner"],
          "properties": {
            "id": { "type": "integer", "minimum": 1, "maximum": 99 },
            "name": { "type": "string", "minLength": 2, "maxLength": 40 },
            "status": { "type": "string", "enum": ["available", "sold"] },
            "price": { "type": "number", "minimum": 5, "maximum": 10 },
            "email": { "type": "string", "format": "email" },
            "createdAt": { "type": "string", "format": "date-time" },
            "code": { "type": "string", "pattern": "^[A-Z]{3}-\\d{4}$" },
            "tags": { "type": "array", "minItems": 2, "maxItems": 4, "items": { "type": "string" } },
            "owner": { "type": "object", "required": ["firstName"], "properties": { "firstName": { "type": "string" }, "city": { "type": "string" } } }
          }
        }
        """;

    [Fact]
    public void Generated_data_validates_against_its_schema_and_varies()
    {
        var faker = new SchemaFaker(new Faker(new Random(5)), new Random(5));
        var values = new HashSet<string>();
        for (var i = 0; i < 25; i++)
        {
            var json = faker.Generate(JsonNode.Parse(PetSchema))!.ToJsonString();
            Assert.Empty(JsonSchemaValidator.Validate(json, PetSchema));
            values.Add(json);
            var pet = JsonNode.Parse(json)!;
            if (pet["code"] is { } code)
                Assert.Matches(@"^[A-Z]{3}-\d{4}$", code.GetValue<string>());
            if (pet["email"] is { } email) // optional: sometimes left out, like real APIs
                Assert.Contains("@", email.GetValue<string>());
        }
        Assert.True(values.Count > 20);
    }

    [Fact]
    public void Property_names_drive_realistic_values()
    {
        var faker = new SchemaFaker(new Faker(new Random(1)), new Random(1));
        var user = faker.Generate(JsonNode.Parse("""
            {"type":"object","properties":{"firstName":{"type":"string"},"email":{"type":"string"},"city":{"type":"string"},
             "avatarUrl":{"type":"string"},"updated_at":{"type":"string"},"format":{"type":"string"}}}
            """))!;
        Assert.Matches("^[A-Z][a-z]+$", user["firstName"]!.GetValue<string>());
        Assert.Contains("@", user["email"]!.GetValue<string>());
        Assert.StartsWith("https://", user["avatarUrl"]!.GetValue<string>());
        Assert.True(DateTimeOffset.TryParse(user["updated_at"]!.GetValue<string>(), out _));
        Assert.False(DateTimeOffset.TryParse(user["format"]!.GetValue<string>(), out _));
    }

    [Fact]
    public void Infers_a_schema_from_an_example_and_generates_the_same_shape()
    {
        var sample = JsonNode.Parse("""{"id":7,"email":"a@b.io","at":"2024-01-02T03:04:05Z","items":[{"sku":"x","qty":2.5}],"ok":true}""");
        var schema = SchemaFaker.InferSchema(sample);
        Assert.Equal("email", schema["properties"]!["email"]!["format"]!.GetValue<string>());
        Assert.Equal("date-time", schema["properties"]!["at"]!["format"]!.GetValue<string>());
        var generated = new SchemaFaker().GenerateLike(sample)!;
        Assert.Empty(JsonSchemaValidator.Validate(generated.ToJsonString(), schema.ToJsonString()));
        Assert.NotEqual(sample!.ToJsonString(), generated.ToJsonString());
    }

    [Fact]
    public void OpenApi_import_keeps_response_schemas_with_refs_inlined()
    {
        var spec = JsonNode.Parse("""
            {"openapi":"3.0.0","info":{"title":"Pets","version":"1"},"paths":{"/pets/{id}":{"get":{"responses":{"200":{"description":"ok",
              "content":{"application/json":{"schema":{"$ref":"#/components/schemas/Pet"}}}}}}}},
             "components":{"schemas":{"Pet":{"type":"object","properties":{"name":{"type":"string"},"friend":{"$ref":"#/components/schemas/Pet"}}}}}}
            """)!;
        var example = OpenApi.Import(spec).Collections[0].Requests[0].Examples[0];
        var schema = JsonNode.Parse(example.Schema)!;
        Assert.Equal("string", schema["properties"]!["name"]!["type"]!.GetValue<string>());
        Assert.Equal("object", schema["properties"]!["friend"]!["type"]!.GetValue<string>()); // recursive ref cut off safely
    }
}

public sealed class SmartMockServerTests
{
    private static readonly HttpClient Http = new();

    private static ApiRequest Route(HttpVerb method, string url, string body, int status = 200, string schema = "") => new()
    {
        Name = $"{method} {url}", Method = method, Url = url,
        Examples = [new ResponseExample { StatusCode = status, Body = body, Schema = schema }]
    };

    private static async Task<(HttpStatusCode Status, JsonNode? Json)> SendAsync(HttpMethod method, string url, string? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    [Fact]
    public async Task Dynamic_mode_generates_fresh_valid_data_from_the_schema()
    {
        const string schema = """{"type":"object","required":["id","name"],"properties":{"id":{"type":"integer","minimum":1},"name":{"type":"string"},"email":{"type":"string","format":"email"}}}""";
        await using var server = new MockServer();
        await server.StartAsync([Route(HttpVerb.Get, "{{baseUrl}}/users/{{id}}", """{"id":1,"name":"Ann","email":"a@b.c"}""", schema: schema)],
            new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort(), DynamicData = true });

        var bodies = new HashSet<string>();
        for (var i = 0; i < 5; i++)
        {
            var (status, json) = await SendAsync(HttpMethod.Get, $"{server.BaseUrl}users/{i}");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Empty(JsonSchemaValidator.Validate(json!.ToJsonString(), schema));
            bodies.Add(json.ToJsonString());
        }
        Assert.True(bodies.Count >= 4);
    }

    [Fact]
    public async Task Stateful_mode_supports_create_read_update_delete()
    {
        await using var server = new MockServer();
        await server.StartAsync(
        [
            Route(HttpVerb.Get, "{{baseUrl}}/pets", """{"data":[{"id":1,"name":"Rex"},{"id":2,"name":"Tom"}],"total":2}"""),
            Route(HttpVerb.Post, "{{baseUrl}}/pets", """{"id":0,"name":"x","status":"available"}""", 201),
            Route(HttpVerb.Get, "{{baseUrl}}/pets/{{petId}}", """{"id":1,"name":"Rex"}"""),
            Route(HttpVerb.Patch, "{{baseUrl}}/pets/{{petId}}", "{}"),
            Route(HttpVerb.Delete, "{{baseUrl}}/pets/{{petId}}", "", 204)
        ], new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort(), Stateful = true });
        var baseUrl = server.BaseUrl!.ToString().TrimEnd('/');

        var (_, list) = await SendAsync(HttpMethod.Get, $"{baseUrl}/pets");
        Assert.Equal(2, list!["data"]!.AsArray().Count);

        var (created, pet) = await SendAsync(HttpMethod.Post, $"{baseUrl}/pets", """{"name":"Luna"}""");
        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal(3, pet!["id"]!.GetValue<long>());
        Assert.Equal("available", pet["status"]!.GetValue<string>()); // from the POST example

        var (_, afterCreate) = await SendAsync(HttpMethod.Get, $"{baseUrl}/pets");
        Assert.Equal(3, afterCreate!["total"]!.GetValue<int>());

        var (patched, updated) = await SendAsync(HttpMethod.Patch, $"{baseUrl}/pets/3", """{"status":"sold","id":99}""");
        Assert.Equal(HttpStatusCode.OK, patched);
        Assert.Equal("Luna", updated!["name"]!.GetValue<string>());
        Assert.Equal("sold", updated["status"]!.GetValue<string>());
        Assert.Equal(3, updated["id"]!.GetValue<long>());

        var (deleted, _) = await SendAsync(HttpMethod.Delete, $"{baseUrl}/pets/1");
        Assert.Equal(HttpStatusCode.NoContent, deleted);
        var (missing, error) = await SendAsync(HttpMethod.Get, $"{baseUrl}/pets/1");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Contains("No item", error!["error"]!.GetValue<string>());

        server.ResetState();
        var (_, reset) = await SendAsync(HttpMethod.Get, $"{baseUrl}/pets");
        Assert.Equal(2, reset!["data"]!.AsArray().Count);
    }

    [Fact]
    public async Task Examples_can_echo_the_request_body_and_headers()
    {
        await using var server = new MockServer();
        await server.StartAsync([Route(HttpVerb.Post, "/orders",
                """{"customer":"{{body.customer.name}}","via":"{{header.x-client}}","method":"{{method}}","ref":"{{$randomAlphaNumeric(6)}}"}""", 201)],
            new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort(), DynamicData = true });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{server.BaseUrl}orders")
        {
            Content = new StringContent("""{"customer":{"name":"Ada"}}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Client", "tests");
        using var response = await Http.SendAsync(request);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("Ada", json["customer"]!.GetValue<string>());
        Assert.Equal("tests", json["via"]!.GetValue<string>());
        Assert.Equal("POST", json["method"]!.GetValue<string>());
        Assert.Equal(6, json["ref"]!.GetValue<string>().Length);
    }
}
