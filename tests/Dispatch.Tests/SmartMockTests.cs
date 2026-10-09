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
    public void Local_refs_in_a_hand_written_schema_are_resolved()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","required":["owner","tags"],"properties":{"owner":{"$ref":"#/$defs/Person"},"tags":{"type":"array","minItems":1,"items":{"$ref":"#/$defs/Tag"}}},
             "$defs":{"Person":{"type":"object","required":["email"],"properties":{"email":{"type":"string","format":"email"}}},"Tag":{"type":"string","enum":["a","b"]}}}
            """);
        var generated = new SchemaFaker(new Faker(new Random(3)), new Random(3)).Generate(schema)!;
        Assert.Contains("@", generated["owner"]!["email"]!.GetValue<string>());
        Assert.All(generated["tags"]!.AsArray(), t => Assert.Contains(t!.GetValue<string>(), new[] { "a", "b" }));
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
        for (var i = 1; i <= 5; i++)
        {
            var (status, json) = await SendAsync(HttpMethod.Get, $"{server.BaseUrl}users/{i}");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Empty(JsonSchemaValidator.Validate(json!.ToJsonString(), schema));
            Assert.Equal(i, json["id"]!.GetValue<long>()); // generated data agrees with the URL
            bodies.Add(json.ToJsonString());
        }
        Assert.True(bodies.Count >= 4);
    }

    [Fact]
    public async Task Dynamic_data_reflects_every_path_parameter()
    {
        await using var server = new MockServer();
        await server.StartAsync([Route(HttpVerb.Get, "{{baseUrl}}/users/{{userId}}/orders/{{orderId}}", """{"id":1,"userId":5,"ref":"A1","total":9.5}""")],
            new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort(), DynamicData = true });

        var (_, order) = await SendAsync(HttpMethod.Get, $"{server.BaseUrl}users/42/orders/7");
        Assert.Equal(42, order!["userId"]!.GetValue<long>());
        Assert.Equal(7, order["id"]!.GetValue<long>()); // orderId has no property of its own, so it lands on id
        Assert.NotEqual("A1", order["ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task Request_and_example_names_with_non_ascii_characters_are_served()
    {
        await using var server = new MockServer();
        await server.StartAsync([new ApiRequest
        {
            Name = "Créer un utilisateur 🚀", Method = HttpVerb.Get, Url = "/users",
            Examples = [new ResponseExample { Name = "Réponse", Body = "[]", Headers = [new("X-Note", "Café {{$guid}}")] }]
        }], new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort() });

        using var response = await Http.GetAsync($"{server.BaseUrl}users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // Kestrel would otherwise fail the response with a 500
        Assert.Equal("Cr?er un utilisateur ?? / R?ponse", response.Headers.GetValues("X-Mock-Match").Single());
        Assert.StartsWith("Caf? ", response.Headers.GetValues("X-Note").Single());
    }

    [Fact]
    public async Task Clients_pick_an_example_with_prefer_headers_and_rules_see_path_parameters()
    {
        await using var server = new MockServer();
        await server.StartAsync([new ApiRequest
        {
            Name = "Get user", Method = HttpVerb.Get, Url = "{{baseUrl}}/users/{{id}}",
            Examples =
            [
                new ResponseExample { Name = "Found", StatusCode = 200, Body = """{"id":"{{id}}"}""" },
                new ResponseExample { Name = "Not found", StatusCode = 404, Body = """{"error":"nope"}""" },
                new ResponseExample { Name = "Banned", StatusCode = 403, Body = """{"error":"banned"}""", MatchQuery = [new("id", "666")] }
            ]
        }], new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort() });

        var (plain, _) = await SendAsync(HttpMethod.Get, $"{server.BaseUrl}users/1");
        Assert.Equal(HttpStatusCode.OK, plain);

        var (banned, _) = await SendAsync(HttpMethod.Get, $"{server.BaseUrl}users/666"); // rule on a path parameter
        Assert.Equal(HttpStatusCode.Forbidden, banned);

        using var byCode = new HttpRequestMessage(HttpMethod.Get, $"{server.BaseUrl}users/1");
        byCode.Headers.Add("Prefer", "code=404");
        using var notFound = await Http.SendAsync(byCode);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("Get user / Not found", notFound.Headers.GetValues("X-Mock-Match").Single());

        using var byName = new HttpRequestMessage(HttpMethod.Get, $"{server.BaseUrl}users/1");
        byName.Headers.Add("X-Mock-Example", "not found");
        using var named = await Http.SendAsync(byName);
        Assert.Equal(HttpStatusCode.NotFound, named.StatusCode);

        using var head = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{server.BaseUrl}users/1"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode); // HEAD is answered by the GET route
    }

    [Fact]
    public async Task Stateful_lists_filter_sort_and_page_and_nested_resources_belong_to_their_parent()
    {
        await using var server = new MockServer();
        await server.StartAsync(
        [
            Route(HttpVerb.Get, "{{baseUrl}}/pets", """{"data":[{"id":1,"name":"Rex","kind":"dog","price":20},{"id":2,"name":"Tom","kind":"cat","price":5},{"id":3,"name":"Rio","kind":"dog","price":12}],"total":3}"""),
            Route(HttpVerb.Post, "{{baseUrl}}/pets", "{}", 201),
            Route(HttpVerb.Get, "{{baseUrl}}/users/{{userId}}/orders", "[]"),
            Route(HttpVerb.Post, "{{baseUrl}}/users/{{userId}}/orders", "{}", 201),
            Route(HttpVerb.Get, "{{baseUrl}}/users/{{userId}}/orders/{{orderId}}", "{}")
        ], new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort(), Stateful = true });
        var baseUrl = server.BaseUrl!.ToString().TrimEnd('/');

        var (_, dogs) = await SendAsync(HttpMethod.Get, $"{baseUrl}/pets?kind=dog&sort=-price");
        Assert.Equal(["Rex", "Rio"], dogs!["data"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
        Assert.Equal(2, dogs["total"]!.GetValue<int>());

        var (_, cheap) = await SendAsync(HttpMethod.Get, $"{baseUrl}/pets?price_lte=12&name_like=r");
        Assert.Equal(["Rio"], cheap!["data"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));

        using var paged = await Http.GetAsync($"{baseUrl}/pets?_sort=name&_page=2&_limit=2");
        var page = JsonNode.Parse(await paged.Content.ReadAsStringAsync())!;
        Assert.Equal(["Tom"], page["data"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
        Assert.Equal("3", paged.Headers.GetValues("X-Total-Count").Single());

        var (_, search) = await SendAsync(HttpMethod.Get, $"{baseUrl}/pets?q=TOM&expand=1"); // unknown keys are ignored
        Assert.Single(search!["data"]!.AsArray());

        using var create = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/users/7/orders") { Content = new StringContent("""{"item":"ball"}""", Encoding.UTF8, "application/json") };
        using var created = await Http.SendAsync(create);
        var order = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(7, order["userId"]!.GetValue<long>());
        Assert.Equal("/users/7/orders/1", created.Headers.Location!.ToString());

        var (_, mine) = await SendAsync(HttpMethod.Get, $"{baseUrl}/users/7/orders");
        Assert.Single(mine!.AsArray());
        var (_, theirs) = await SendAsync(HttpMethod.Get, $"{baseUrl}/users/8/orders");
        Assert.Empty(theirs!.AsArray());
        var (wrongParent, _) = await SendAsync(HttpMethod.Get, $"{baseUrl}/users/8/orders/1");
        Assert.Equal(HttpStatusCode.NotFound, wrongParent);

        var (duplicate, _) = await SendAsync(HttpMethod.Post, $"{baseUrl}/pets", """{"id":1,"name":"Again"}""");
        Assert.Equal(HttpStatusCode.Conflict, duplicate);
    }

    [Fact]
    public async Task State_can_be_exported_and_imported()
    {
        await using var server = new MockServer();
        await server.StartAsync([Route(HttpVerb.Get, "/pets", "[]"), Route(HttpVerb.Post, "/pets", "{}", 201)],
            new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort(), Stateful = true });
        var baseUrl = server.BaseUrl!.ToString().TrimEnd('/');

        Assert.Equal(1, server.ImportState(JsonNode.Parse("""{"pets":[{"id":10,"name":"Imported"}]}""")!.AsObject()));
        var (_, list) = await SendAsync(HttpMethod.Get, $"{baseUrl}/pets");
        Assert.Equal("Imported", list!.AsArray().Single()!["name"]!.GetValue<string>());

        await SendAsync(HttpMethod.Post, $"{baseUrl}/pets", """{"name":"New"}""");
        var exported = server.ExportState()!;
        Assert.Equal([10L, 11L], exported["/pets"]!.AsArray().Select(p => p!["id"]!.GetValue<long>()));
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
    public async Task Stateful_mode_assigns_sequential_ids_across_repeated_posts()
    {
        await using var server = new MockServer();
        await server.StartAsync(
        [
            Route(HttpVerb.Get, "{{baseUrl}}/pets", """[{"id":1,"name":"Rex"}]"""),
            Route(HttpVerb.Post, "{{baseUrl}}/pets", """{"id":0,"name":"x"}""", 201)
        ], new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort(), Stateful = true });
        var baseUrl = server.BaseUrl!.ToString().TrimEnd('/');

        var (first, luna) = await SendAsync(HttpMethod.Post, $"{baseUrl}/pets", """{"name":"Luna"}""");
        Assert.Equal(HttpStatusCode.Created, first);
        Assert.Equal(2, luna!["id"]!.GetValue<long>());

        // The second POST must compute the next id from the id the first POST created (a JsonValue<long>), not crash.
        var (second, milo) = await SendAsync(HttpMethod.Post, $"{baseUrl}/pets", """{"name":"Milo"}""");
        Assert.Equal(HttpStatusCode.Created, second);
        Assert.Equal(3, milo!["id"]!.GetValue<long>());
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

public sealed class SeededMockServerTests
{
    [Fact]
    public async Task Seeded_dynamic_data_is_generated_safely_under_concurrent_requests()
    {
        const string schema = """{"type":"object","required":["id","name","tags"],"properties":{"id":{"type":"integer","minimum":1},"name":{"type":"string"},"tags":{"type":"array","items":{"type":"string"},"minItems":1}}}""";
        await using var server = new MockServer();
        await server.StartAsync([new ApiRequest
        {
            Name = "GET users", Method = HttpVerb.Get, Url = "{{baseUrl}}/users/{{id}}",
            Examples = [new ResponseExample { StatusCode = 200, Body = """{"id":1,"name":"Ann","tags":["a"]}""", Schema = schema }]
        }], new MockServerOptions { Port = HttpProtocolExecutorTests.FreePort(), DynamicData = true, Seed = 42 });

        using var http = new HttpClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 64).Select(async i =>
        {
            using var response = await http.GetAsync($"{server.BaseUrl}users/{i + 1}");
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        }));

        Assert.All(responses, r =>
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Empty(JsonSchemaValidator.Validate(r.Body, schema));
        });
    }
}
