using System.Text.Json.Nodes;
using Dispatch.Application.Help;
using Dispatch.Application.Interop;
using Dispatch.Application.Spec;
using Dispatch.Application.Testing;
using Dispatch.Domain;
using Dispatch.Infrastructure.Interop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Tests;

public class OpenApiWriterTests
{
    private static RequestCollection PetStore()
    {
        var collection = new RequestCollection
        {
            Name = "Pet Store",
            Variables = [new KeyValueItem("baseUrl", "https://api.pets.test/v1"), new KeyValueItem("petId", "7")]
        };
        collection.Requests.Add(new ApiRequest
        {
            Name = "Get a pet", Folder = "pets", Method = HttpVerb.Get, CollectionId = collection.Id, SortOrder = 0,
            Url = "{{baseUrl}}/pets/{{petId}}?fields=name&verbose=true",
            QueryParams = [new KeyValueItem("fields", "name"), new KeyValueItem("verbose", "true", enabled: false)],
            Headers = [new KeyValueItem("X-Tenant", "acme")],
            Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "{{token}}" },
            Examples =
            [
                new ResponseExample { StatusCode = 200, Body = """{"id":7,"name":"Rex","tag":null,"born":"2020-05-01","owner":{"email":"ann@example.com"}}""" },
                new ResponseExample { StatusCode = 200, Body = """{"id":8,"name":"Tom","tag":"cat","born":"2019-01-12","owner":{"email":"bob@example.com"}}""" },
                new ResponseExample { StatusCode = 404, Body = """{"error":"not found"}""" }
            ]
        });
        collection.Requests.Add(new ApiRequest
        {
            Name = "Create a pet", Folder = "pets", Method = HttpVerb.Post, CollectionId = collection.Id, SortOrder = 1,
            Url = "{{baseUrl}}/pets",
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"name":"Rex","kind":"dog"}""" },
            Auth = new AuthSettings { Mode = AuthMode.ApiKey, ApiKeyName = "X-API-Key" },
            Examples = [new ResponseExample { StatusCode = 201, Body = """{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","name":"Rex","kind":"dog"}""" }]
        });
        collection.Requests.Add(new ApiRequest
        {
            Name = "Delete order", Method = HttpVerb.Delete, CollectionId = collection.Id, SortOrder = 2,
            Url = "https://api.pets.test/v1/users/42/orders/9001"
        });
        return collection;
    }

    [Fact]
    public void Describes_paths_parameters_schemas_and_security_from_a_collection()
    {
        var spec = OpenApiWriter.FromCollection(PetStore());

        Assert.Equal("3.0.3", spec["openapi"]!.ToString());
        Assert.Equal("Pet Store", spec["info"]!["title"]!.ToString());
        Assert.Equal("https://api.pets.test/v1", spec["servers"]![0]!["url"]!.ToString());

        var get = spec["paths"]!["/pets/{petId}"]!["get"]!;
        Assert.Equal("Get a pet", get["summary"]!.ToString());
        Assert.Equal("getPetsByPetId", get["operationId"]!.ToString());
        Assert.Equal("pets", get["tags"]![0]!.ToString());
        var parameters = get["parameters"]!.AsArray();
        var petId = parameters.Single(p => p!["name"]!.ToString() == "petId");
        Assert.Equal("path", petId!["in"]!.ToString());
        Assert.Equal("integer", petId["schema"]!["type"]!.ToString());
        Assert.True(parameters.Single(p => p!["name"]!.ToString() == "fields")!["required"]!.GetValue<bool>());
        Assert.False(parameters.Single(p => p!["name"]!.ToString() == "verbose")!["required"]!.GetValue<bool>());
        Assert.Equal("header", parameters.Single(p => p!["name"]!.ToString() == "X-Tenant")!["in"]!.ToString());

        var ok = get["responses"]!["200"]!["content"]!["application/json"]!;
        var schema = ok["schema"]!;
        Assert.Equal("object", schema["type"]!.ToString());
        Assert.Equal("integer", schema["properties"]!["id"]!["type"]!.ToString());
        Assert.True(schema["properties"]!["tag"]!["nullable"]!.GetValue<bool>());
        Assert.Equal("date", schema["properties"]!["born"]!["format"]!.ToString());
        Assert.Equal("email", schema["properties"]!["owner"]!["properties"]!["email"]!["format"]!.ToString());
        Assert.Contains("name", schema["required"]!.AsArray().Select(r => r!.ToString()));
        Assert.Equal("Rex", ok["example"]!["name"]!.ToString());
        Assert.Equal("Not Found", get["responses"]!["404"]!["description"]!.ToString());
        Assert.Equal("bearerAuth", get["security"]![0]!.AsObject().First().Key);

        var post = spec["paths"]!["/pets"]!["post"]!;
        Assert.True(post["requestBody"]!["required"]!.GetValue<bool>());
        Assert.Equal("string", post["requestBody"]!["content"]!["application/json"]!["schema"]!["properties"]!["kind"]!["type"]!.ToString());
        Assert.Equal("uuid", post["responses"]!["201"]!["content"]!["application/json"]!["schema"]!["properties"]!["id"]!["format"]!.ToString());
        Assert.Equal("apiKeyAuth", post["security"]![0]!.AsObject().First().Key);

        var schemes = spec["components"]!["securitySchemes"]!;
        Assert.Equal("bearer", schemes["bearerAuth"]!["scheme"]!.ToString());
        Assert.Equal("X-API-Key", schemes["apiKeyAuth"]!["name"]!.ToString());

        // Absolute URLs with id-like segments are templated after the preceding segment; no example → default 200.
        var delete = spec["paths"]!["/users/{userId}/orders/{orderId}"]!["delete"]!;
        Assert.Equal("OK", delete["responses"]!["200"]!["description"]!.ToString());
        Assert.Equal("42", delete["parameters"]![0]!["example"]!.ToString());
    }

    [Fact]
    public void Distinct_auth_configurations_get_distinct_security_schemes()
    {
        var collection = new RequestCollection { Name = "Keys", Variables = [new KeyValueItem("baseUrl", "https://k.test")] };
        collection.Requests.Add(new ApiRequest { Name = "A", Url = "{{baseUrl}}/a", Auth = new AuthSettings { Mode = AuthMode.ApiKey, ApiKeyName = "X-API-Key" } });
        collection.Requests.Add(new ApiRequest { Name = "B", Url = "{{baseUrl}}/b", Auth = new AuthSettings { Mode = AuthMode.ApiKey, ApiKeyName = "api_key", ApiKeyLocation = ApiKeyLocation.QueryParam } });
        collection.Requests.Add(new ApiRequest { Name = "C", Url = "{{baseUrl}}/c", Auth = new AuthSettings { Mode = AuthMode.ApiKey, ApiKeyName = "X-API-Key" } });

        var spec = OpenApiWriter.FromCollection(collection);
        var schemes = spec["components"]!["securitySchemes"]!.AsObject();
        Assert.Equal(["apiKeyAuth", "apiKeyAuth2"], schemes.Select(s => s.Key));
        Assert.Equal("query", schemes["apiKeyAuth2"]!["in"]!.ToString());
        Assert.Equal("apiKeyAuth2", spec["paths"]!["/b"]!["get"]!["security"]![0]!.AsObject().First().Key);
        Assert.Equal("apiKeyAuth", spec["paths"]!["/c"]!["get"]!["security"]![0]!.AsObject().First().Key);
    }

    [Fact]
    public void Uses_history_of_this_collection_only()
    {
        var collection = PetStore();
        var get = collection.Requests.Single(r => r.Name == "Get a pet");
        static HistoryEntry Entry(ApiRequest request, string url, string body) => new()
        {
            Method = HttpVerb.Get, Url = url, Request = request,
            Response = new ResponseSnapshot { StatusCode = 200, EffectiveUrl = url, Body = body }
        };
        var otherApi = new ApiRequest { Name = "Other", Url = "https://other.test/pets/1", CollectionId = Guid.NewGuid() };
        var history = new List<HistoryEntry>
        {
            Entry(get, "https://api.pets.test/v1/pets/7", """{"id":7,"name":"Rex","weight":12}"""),
            Entry(otherApi, "https://other.test/pets/1", """{"id":1,"name":"x","foreign":true}""")
        };

        var schema = OpenApiWriter.FromCollection(collection, history)["paths"]!["/pets/{petId}"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        Assert.NotNull(schema["properties"]!["weight"]);
        Assert.Null(schema["properties"]!["foreign"]);
    }

    [Theory]
    [InlineData("/users/42/orders/7", "/users/{userId}/orders/{orderId}")]
    [InlineData("/v2/items/3fa85f64-5717-4562-b3fc-2c963f66afa6", "/v2/items/{itemId}")]
    [InlineData("/pets/{{petId}}/photos", "/pets/{petId}/photos")]
    [InlineData("/categories/12/stats", "/categories/{categoryId}/stats")]
    [InlineData("/42", "/{id}")]
    [InlineData("/health", "/health")]
    public void Templates_paths(string path, string expected) => Assert.Equal(expected, OpenApiWriter.TemplateOf(path));

    [Fact]
    public void Generated_yaml_is_read_back_identically_and_quotes_only_what_yaml_would_misread()
    {
        var spec = OpenApiWriter.FromCollection(PetStore());
        spec["info"]!["description"] = "yes: it's \"quoted\" #not a comment\nsecond line";
        ((JsonObject)spec["info"]!)["x-dates"] = new JsonArray("2024-01-01", "007", "1e3", "on", "", " padded ", "plain text");
        var yaml = OpenApiWriter.ToYaml(spec);

        Assert.StartsWith("openapi: 3.0.3\n", yaml);
        Assert.Contains("\"200\":\n", yaml);
        Assert.Contains("- \"2024-01-01\"", yaml);
        Assert.Contains("- \"007\"", yaml);
        Assert.Contains("- \"on\"", yaml);
        Assert.Contains("- plain text", yaml);
        Assert.Contains("- name: petId\n", yaml);

        var reread = Yaml.ToJson(yaml);
        Assert.Equal(spec.ToJsonString(), reread!.ToJsonString());
    }

    [Fact]
    public void Round_trips_through_the_openapi_importer()
    {
        var spec = OpenApiWriter.FromCollection(PetStore());
        var imported = Importer.ImportText(OpenApiWriter.ToYaml(spec), "generated", "/specs/generated.yaml");
        var collection = imported.Collections.Single();

        Assert.StartsWith("OpenAPI 3.0", imported.Format);
        Assert.Equal("Pet Store", collection.Name);
        Assert.Equal("https://api.pets.test/v1", collection.Variables.Single(v => v.Key == "baseUrl").Value);
        Assert.Equal(3, collection.Requests.Count);

        var get = collection.Requests.Single(r => r.Name == "Get a pet");
        Assert.Equal("{{baseUrl}}/pets/{{petId}}", get.Url.Split('?')[0]);
        Assert.Equal("7", collection.Variables.Single(v => v.Key == "petId").Value);
        Assert.Equal(AuthMode.Bearer, get.Auth.Mode);
        Assert.Contains(get.Assertions, a => a is { Source: ValueSource.Status, Expected: "200" });
        Assert.Contains(get.Examples, e => e.StatusCode == 200 && e.Body.Contains("Rex"));

        var create = collection.Requests.Single(r => r.Name == "Create a pet");
        Assert.Equal(BodyMode.Json, create.Body.Mode);
        Assert.Equal("dog", JsonNode.Parse(create.Body.Content)!["kind"]!.ToString());
        Assert.Equal(AuthMode.ApiKey, create.Auth.Mode);
    }

    [Fact]
    public void Describes_captured_traffic_from_a_har_file()
    {
        static JsonObject Entry(string method, string url, int status, string body, string? auth = null, string? postBody = null) => new()
        {
            ["startedDateTime"] = "2026-01-01T10:00:00Z",
            ["request"] = new JsonObject
            {
                ["method"] = method, ["url"] = url,
                ["headers"] = new JsonArray(auth is null ? [] : [new JsonObject { ["name"] = "Authorization", ["value"] = auth }]),
                ["postData"] = postBody is null ? null : new JsonObject { ["mimeType"] = "application/json", ["text"] = postBody }
            },
            ["response"] = new JsonObject
            {
                ["status"] = status,
                ["content"] = new JsonObject { ["mimeType"] = "application/json", ["text"] = body }
            }
        };
        var har = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["entries"] = new JsonArray(
                    Entry("GET", "https://api.shop.test/users/alice?expand=orders", 200, """{"name":"alice","role":"admin"}""", "Bearer abc"),
                    Entry("GET", "https://api.shop.test/users/bob", 200, """{"name":"bob","role":"user"}""", "Bearer abc"),
                    Entry("GET", "https://api.shop.test/users/carol", 200, """{"name":"carol","role":"user"}""", "Bearer abc"),
                    Entry("GET", "https://api.shop.test/users/dave", 404, """{"error":"gone"}""", "Bearer abc"),
                    Entry("POST", "https://api.shop.test/orders", 201, """{"id":1,"status":"paid"}""", "Bearer abc", """{"sku":"A1","qty":2}"""),
                    Entry("POST", "https://api.shop.test/orders", 201, """{"id":2,"status":"paid"}""", "Bearer abc", """{"sku":"B2","qty":1}"""),
                    Entry("GET", "https://cdn.shop.test/app.js", 200, "console.log(1)"))
            }
        };

        var spec = OpenApiWriter.FromHar(har);

        Assert.Equal("https://api.shop.test", spec["servers"]![0]!["url"]!.ToString());
        Assert.Equal("api.shop.test API", spec["info"]!["title"]!.ToString());
        var paths = spec["paths"]!.AsObject();
        Assert.Equal(["/users/{userId}", "/orders"], paths.Select(p => p.Key));

        var users = paths["/users/{userId}"]!["get"]!;
        Assert.Equal("users", users["tags"]![0]!.ToString());
        Assert.False(users["parameters"]!.AsArray().Single(p => p!["name"]!.ToString() == "expand")!["required"]!.GetValue<bool>());
        Assert.Equal(["200", "404"], users["responses"]!.AsObject().Select(r => r.Key));
        Assert.Equal("bearerAuth", users["security"]![0]!.AsObject().First().Key);

        var orders = paths["/orders"]!["post"]!;
        Assert.Equal("integer", orders["requestBody"]!["content"]!["application/json"]!["schema"]!["properties"]!["qty"]!["type"]!.ToString());
        Assert.Equal(2, OpenApiWriter.OperationCount(spec));
    }
}

public class SchemaInferrerTests
{
    private static JsonObject Infer(params string[] samples) => SchemaInferrer.Infer(samples.Select(s => JsonNode.Parse(s)).ToList());

    [Fact]
    public void Merges_objects_marking_fields_present_everywhere_as_required()
    {
        var schema = Infer("""{"id":1,"name":"a","extra":true}""", """{"id":2,"name":null}""");
        Assert.Equal(["id", "name"], schema["required"]!.AsArray().Select(r => r!.ToString()));
        Assert.True(schema["properties"]!["name"]!["nullable"]!.GetValue<bool>());
        Assert.Equal("string", schema["properties"]!["name"]!["type"]!.ToString());
        Assert.Equal("boolean", schema["properties"]!["extra"]!["type"]!.ToString());
    }

    [Fact]
    public void Infers_arrays_numbers_formats_enums_and_mixed_types()
    {
        var schema = Infer("""{"items":[{"price":1.5,"when":"2024-01-01T10:00:00Z"},{"price":2,"when":"2024-01-02T10:00:00Z"}],"mixed":1}""",
            """{"items":[],"mixed":"one"}""");
        Assert.Equal("number", schema["properties"]!["items"]!["items"]!["properties"]!["price"]!["type"]!.ToString());
        Assert.Equal("date-time", schema["properties"]!["items"]!["items"]!["properties"]!["when"]!["format"]!.ToString());
        Assert.Equal(2, schema["properties"]!["mixed"]!["oneOf"]!.AsArray().Count);
        // Integers fold into number even next to another type, so no sample matches two oneOf branches.
        var tri = Infer("1", "1.5", "\"unknown\"")["oneOf"]!.AsArray();
        Assert.Equal(["number", "string"], tri.Select(b => b!["type"]!.ToString()).Order());

        var statuses = Infer("\"paid\"", "\"shipped\"", "\"paid\"", "\"shipped\"", "\"paid\"");
        Assert.Equal(["paid", "shipped"], statuses["enum"]!.AsArray().Select(e => e!.ToString()));

        // Values that never repeat are not an enumeration; nulls alone are just nullable.
        Assert.Null(Infer("\"a\"", "\"b\"", "\"c\"", "\"d\"")["enum"]);
        Assert.True(Infer("null")["nullable"]!.GetValue<bool>());
    }

    [Fact]
    public void Parameter_values_are_typed_from_their_text()
    {
        JsonObject Scalar(params string[] values) => SchemaInferrer.Infer(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToList(), true);
        Assert.Equal("integer", Scalar("1", "42")["type"]!.ToString());
        Assert.Equal("boolean", Scalar("true", "false")["type"]!.ToString());
        Assert.Equal("string", Scalar("1", "abc")["type"]!.ToString());
        Assert.Equal("uuid", Scalar("3fa85f64-5717-4562-b3fc-2c963f66afa6")["format"]!.ToString());
    }
}

public class TestScaffolderTests
{
    internal const string Spec = """
        openapi: 3.0.3
        info: { title: Orders, version: "1.0" }
        servers:
          - url: https://orders.test
        components:
          schemas:
            Order:
              type: object
              required: [sku, qty]
              properties:
                id: { type: integer, readOnly: true }
                sku: { type: string, example: A1 }
                qty: { type: integer, example: 2 }
                note: { type: string }
        paths:
          /orders:
            get:
              summary: List orders
              parameters:
                - { name: limit, in: query, required: true, schema: { type: integer, example: 10 } }
                - { name: X-Tenant, in: header, required: true, schema: { type: string, example: acme } }
                - { name: sort, in: query, schema: { type: string } }
              responses:
                '200':
                  description: OK
                  content:
                    application/json:
                      schema:
                        type: array
                        items: { $ref: '#/components/schemas/Order' }
            post:
              summary: Create order
              requestBody:
                required: true
                content:
                  application/json:
                    schema: { $ref: '#/components/schemas/Order' }
              responses:
                '201':
                  description: Created
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Order' }
          /health:
            get:
              summary: Health
              responses:
                '204': { description: No content }
        """;

    [Fact]
    public void Scaffolds_happy_paths_schema_assertions_and_negatives()
    {
        var report = TestScaffolder.Scaffold(Yaml.ToJson(Spec)!, "/specs/orders.yaml");
        var collection = report.Collection;

        Assert.Equal("Orders tests", collection.Name);
        Assert.Equal(3, report.Operations);
        Assert.Equal(2, report.SchemaAssertions);
        Assert.Equal(4, report.NegativeRequests);
        Assert.Equal(7, collection.Requests.Count);
        Assert.All(collection.Requests, r => Assert.Equal(collection.Id, r.CollectionId));

        var list = collection.Requests.Single(r => r.Name == "List orders");
        Assert.Contains(list.Assertions, a => a is { Source: ValueSource.Status, Expected: "200" });
        Assert.Contains(list.Assertions, a => a.Source == ValueSource.Contract);
        var schema = list.Assertions.Single(a => a.Source == ValueSource.JsonSchema);
        Assert.Empty(JsonSchemaValidator.Validate("""[{"id":1,"sku":"A1","qty":2}]""", schema.Path));
        Assert.NotEmpty(JsonSchemaValidator.Validate("""[{"id":1,"sku":"A1"}]""", schema.Path));

        var health = collection.Requests.Single(r => r.Name == "Health");
        Assert.DoesNotContain(health.Assertions, a => a.Source == ValueSource.JsonSchema);

        // Negatives follow their request, in a sub-folder, with only the 4xx assertions.
        var names = collection.Requests.OrderBy(r => r.SortOrder).Select(r => r.Name).ToList();
        Assert.Equal(["List orders", "List orders — missing query parameter limit", "List orders — missing header X-Tenant",
            "Create order", "Create order — missing body field sku", "Create order — missing body field qty", "Health"], names);
        var missingLimit = collection.Requests.Single(r => r.Name.EndsWith("missing query parameter limit"));
        Assert.Equal("Negative tests", missingLimit.Folder);
        Assert.DoesNotContain("limit", missingLimit.Url);
        Assert.DoesNotContain(missingLimit.QueryParams, q => q.Key == "limit");
        Assert.Contains(missingLimit.Headers, h => h.Key == "X-Tenant");
        Assert.Equal(2, missingLimit.Assertions.Count);
        Assert.Contains(missingLimit.Assertions, a => a is { Source: ValueSource.Status, Operator: AssertionOperator.GreaterOrEqual, Expected: "400" });
        Assert.Contains(missingLimit.Assertions, a => a is { Source: ValueSource.Status, Operator: AssertionOperator.LessThan, Expected: "500" });
        Assert.Empty(missingLimit.Examples);

        var missingSku = collection.Requests.Single(r => r.Name.EndsWith("missing body field sku"));
        var body = JsonNode.Parse(missingSku.Body.Content)!.AsObject();
        Assert.False(body.ContainsKey("sku"));
        Assert.Equal(2, body["qty"]!.GetValue<int>());

        // Running it again changes nothing.
        var again = TestScaffolder.Apply(Yaml.ToJson(Spec)!, collection);
        Assert.Equal(0, again.NegativeRequests + again.SchemaAssertions);
        Assert.Equal(7, collection.Requests.Count);
    }

    [Fact]
    public void Help_has_a_topic_for_spec_generation()
    {
        Assert.Contains(HelpCatalog.Search("openapi scaffold"), t => t.Id == HelpCatalog.SpecGeneration);
        Assert.Contains(HelpCatalog.Search("negative test"), t => t.Id == HelpCatalog.SpecGeneration);
    }
}

[Collection("Console")]
public class CliSpecTests
{
    [Fact]
    public async Task Openapi_command_writes_yaml_or_json_from_a_collection_or_har()
    {
        var dir = Cli.TempDir();
        var collection = new RequestCollection
        {
            Name = "Shop",
            Variables = [new KeyValueItem("baseUrl", "https://shop.test")],
            Requests =
            [
                new ApiRequest
                {
                    Name = "Get item", Url = "{{baseUrl}}/items/12",
                    Examples = [new ResponseExample { StatusCode = 200, Body = """{"id":12,"price":9.5}""" }]
                }
            ]
        };
        var file = Path.Combine(dir, "shop.dispatch.json");
        await File.WriteAllTextAsync(file, DispatchFormat.ExportCollection(collection));

        var yamlPath = Path.Combine(dir, "api.yaml");
        var (exit, output) = await Cli.RunAsync("openapi", file, "-o", yamlPath, "--db", Path.Combine(dir, "d.db"), "--title", "Shop API");
        Assert.Equal(0, exit);
        Assert.Contains("Described 1 operation(s)", output);
        var yaml = await File.ReadAllTextAsync(yamlPath);
        Assert.Contains("title: Shop API", yaml);
        Assert.Contains("/items/{itemId}:", yaml);

        var jsonPath = Path.Combine(dir, "api.json");
        (exit, _) = await Cli.RunAsync("openapi", file, "-o", jsonPath, "--db", Path.Combine(dir, "d.db"), "--server", "https://prod.shop.test");
        Assert.Equal(0, exit);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath))!;
        Assert.Equal("https://prod.shop.test", json["servers"]![0]!["url"]!.ToString());
        Assert.Equal("number", json["paths"]!["/items/{itemId}"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["price"]!["type"]!.ToString());

        var har = Path.Combine(dir, "traffic.har");
        await File.WriteAllTextAsync(har, """
            {"log":{"entries":[{"startedDateTime":"2026-01-01T00:00:00Z","request":{"method":"GET","url":"https://h.test/ping","headers":[]},
             "response":{"status":200,"content":{"mimeType":"application/json","text":"{\"ok\":true}"}}}]}}
            """);
        (exit, output) = await Cli.RunAsync("openapi", har, "-o", "-", "--db", Path.Combine(dir, "d.db"));
        Assert.Equal(0, exit);
        Assert.Contains("/ping:", output);
        Assert.Contains("url: https://h.test", output);

        // On stdout the document is alone: progress goes to stderr, so the output parses as a whole.
        var (stdoutExit, stdoutJson) = await Cli.RunStdoutAsync("openapi", har, "-o", "-", "--format", "json", "--db", Path.Combine(dir, "d.db"));
        Assert.Equal(0, stdoutExit);
        Assert.Equal("3.0.3", JsonNode.Parse(stdoutJson)!["openapi"]!.ToString());

        var (usage, _) = await Cli.RunAsync("openapi", "--db", Path.Combine(dir, "d.db"));
        Assert.Equal(2, usage);
    }

    [Fact]
    public async Task Scaffold_command_writes_a_collection_that_runs_against_the_api()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/orders", (HttpRequest request) =>
                !request.Query.ContainsKey("limit") || !request.Headers.ContainsKey("X-Tenant")
                    ? Results.BadRequest(new { error = "limit and X-Tenant are required" })
                    : Results.Json(new[] { new { id = 1, sku = "A1", qty = 2 } }));
            app.MapPost("/orders", async (HttpRequest request) =>
            {
                var body = await JsonNode.ParseAsync(request.Body);
                return body?["sku"] is null || body["qty"] is null
                    ? Results.UnprocessableEntity(new { error = "sku and qty are required" })
                    : Results.Json(new { id = 2, sku = body["sku"]!.ToString(), qty = body["qty"]!.GetValue<int>() }, statusCode: 201);
            });
            app.MapGet("/health", () => Results.NoContent());
        });
        var dir = Cli.TempDir();
        var spec = Path.Combine(dir, "orders.yaml");
        await File.WriteAllTextAsync(spec, TestScaffolderTests.Spec.Replace("https://orders.test", server.BaseUrl));
        var tests = Path.Combine(dir, "tests.dispatch.json");
        var db = Path.Combine(dir, "d.db");

        var (exit, output) = await Cli.RunAsync("scaffold", spec, "-o", tests, "--db", db);
        Assert.Equal(0, exit);
        Assert.Contains("3 happy-path request(s)", output);
        Assert.Contains("4 negative request(s)", output);
        Assert.Equal(7, DispatchFormat.Import(await File.ReadAllTextAsync(tests)).RequestCount);

        var (stdoutExit, stdoutJson) = await Cli.RunStdoutAsync("scaffold", spec, "-o", "-", "--db", db);
        Assert.Equal(0, stdoutExit);
        Assert.Equal(7, DispatchFormat.Import(stdoutJson).RequestCount);

        var (runExit, runOutput) = await Cli.RunAsync("run", tests, "--db", db, "--no-color");
        Assert.True(runExit == 0, runOutput);

        var (saveExit, saveOutput) = await Cli.RunAsync("scaffold", spec, "--save", "--no-negatives", "--db", db);
        Assert.Equal(0, saveExit);
        Assert.Contains("Saved collection 'Orders tests' (3 requests)", saveOutput);
        var (listExit, listOutput) = await Cli.RunAsync("list", "--db", db);
        Assert.Equal(0, listExit);
        Assert.Contains("Orders tests", listOutput);
    }
}
