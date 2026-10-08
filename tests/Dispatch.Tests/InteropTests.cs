using System.Text.Json.Nodes;
using Dispatch.Application.Interop;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Interop;

namespace Dispatch.Tests;

public class CurlTests
{
    [Fact]
    public void Parses_a_browser_copied_command()
    {
        var request = Curl.Parse("""
            curl 'https://api.example.com/v1/users?page=2' \
              -H 'accept: application/json' \
              -H 'authorization: Bearer abc' \
              --data-raw '{"name":"Ann","note":"it'\''s"}' \
              --compressed
            """);

        Assert.Equal(HttpVerb.Post, request.Method);
        Assert.Equal("https://api.example.com/v1/users?page=2", request.Url);
        Assert.Equal("2", request.QueryParams.Single().Value);
        Assert.Equal(BodyMode.Json, request.Body.Mode);
        Assert.Equal("""{"name":"Ann","note":"it's"}""", request.Body.Content);
        Assert.Contains(request.Headers, h => h.Key == "authorization" && h.Value == "Bearer abc");
    }

    [Fact]
    public void Parses_user_forms_method_and_flags()
    {
        var request = Curl.Parse("curl -X PUT -u bob:s3cret -k -L --max-time 2.5 -F 'name=doc' -F 'file=@/tmp/a.txt;type=text/plain' https://x.io/up");

        Assert.Equal(HttpVerb.Put, request.Method);
        Assert.Equal(AuthMode.Basic, request.Auth.Mode);
        Assert.Equal("s3cret", request.Auth.Password);
        Assert.False(request.Settings.VerifySsl);
        Assert.Equal(2500, request.Settings.TimeoutMs);
        Assert.Equal(BodyMode.Multipart, request.Body.Mode);
        Assert.True(request.Body.FormFields[1].IsFile);
        Assert.Equal("/tmp/a.txt", request.Body.FormFields[1].Value);
    }

    [Fact]
    public void Get_with_data_moves_data_to_the_query()
    {
        var request = Curl.Parse("curl -G https://x.io/search -d q=hello --data-urlencode 'tag=a b'");
        Assert.Equal(HttpVerb.Get, request.Method);
        Assert.Equal("https://x.io/search?q=hello&tag=a%20b", request.Url);
    }

    [Theory]
    [InlineData(CodeTarget.Curl, "curl -X PATCH 'https://api.test/items/7?x=1'")]
    [InlineData(CodeTarget.Python, "requests.request(\"PATCH\"")]
    [InlineData(CodeTarget.CSharp, "new HttpMethod(\"PATCH\")")]
    [InlineData(CodeTarget.JavaScript, "method: \"PATCH\"")]
    [InlineData(CodeTarget.Go, "http.NewRequest(\"PATCH\"")]
    [InlineData(CodeTarget.HttPie, "http PATCH 'https://api.test/items/7?x=1'")]
    public void Generates_code(CodeTarget target, string expected)
    {
        var request = new ApiRequest
        {
            Method = HttpVerb.Patch,
            Url = "https://api.test/items/7?x=1",
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"done":true}""" },
            Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "t" }
        };

        var code = CodeGenerator.Generate(request, target);

        Assert.Contains(expected, code);
        Assert.Contains("Bearer t", code);
    }

    [Fact]
    public void Generated_curl_parses_back_to_the_same_request()
    {
        var original = new ApiRequest
        {
            Method = HttpVerb.Delete,
            Url = "https://api.test/items/7",
            Headers = [new("X-Trace", "it's")],
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"reason":"dup"}""" }
        };

        var parsed = Curl.Parse(CodeGenerator.Generate(original, CodeTarget.Curl));

        Assert.Equal(original.Method, parsed.Method);
        Assert.Equal(original.Url, parsed.Url);
        Assert.Equal(original.Body.Content, parsed.Body.Content);
        Assert.Contains(parsed.Headers, h => h.Key == "X-Trace" && h.Value == "it's");
    }

    [Fact]
    public void Grpcurl_command_for_grpc_requests()
    {
        var code = CodeGenerator.Generate(new ApiRequest
        {
            Kind = RequestKind.Grpc,
            Url = "localhost:5001",
            Headers = [new("x-tenant", "a")],
            Protocol = new ProtocolSettings { Grpc = new GrpcSettings { Service = "pkg.Svc", Method = "Get", Message = """{"id":1}""" } }
        }, CodeTarget.Grpcurl);

        Assert.Contains("grpcurl -plaintext", code);
        Assert.Contains("localhost:5001 pkg.Svc/Get", code);
        Assert.Contains("-d '{\"id\":1}'", code);
    }
}

public class PostmanTests
{
    private const string Collection = """
        {
          "info": { "name": "Shop", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
          "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{token}}" } ] },
          "variable": [ { "key": "base", "value": "https://shop.test" } ],
          "event": [ { "listen": "test", "script": { "exec": ["pm.test('global', () => {});"] } } ],
          "item": [
            {
              "name": "Orders",
              "item": [
                {
                  "name": "Create order",
                  "event": [ { "listen": "prerequest", "script": { "exec": ["pm.variables.set('n', 1);"] } } ],
                  "request": {
                    "method": "POST",
                    "header": [ { "key": "Content-Type", "value": "application/json" }, { "key": "X-Off", "value": "1", "disabled": true } ],
                    "body": { "mode": "raw", "raw": "{\"sku\":\"A\"}", "options": { "raw": { "language": "json" } } },
                    "url": { "raw": "{{base}}/orders/:id?dry=true", "variable": [ { "key": "id", "value": "" } ],
                             "query": [ { "key": "dry", "value": "true" }, { "key": "debug", "value": "1", "disabled": true } ] }
                  },
                  "response": [ { "name": "Created", "code": 201, "header": [], "body": "{\"id\":1}" } ]
                },
                {
                  "name": "Upload",
                  "request": {
                    "method": "POST",
                    "auth": { "type": "basic", "basic": [ { "key": "username", "value": "u" }, { "key": "password", "value": "p" } ] },
                    "body": { "mode": "formdata", "formdata": [ { "key": "f", "type": "file", "src": "/tmp/x.png" }, { "key": "n", "value": "v", "type": "text" } ] },
                    "url": "{{base}}/upload"
                  }
                }
              ]
            },
            {
              "name": "GraphQL",
              "request": { "method": "POST", "body": { "mode": "graphql", "graphql": { "query": "{ me { id } }", "variables": "" } }, "url": "{{base}}/graphql" }
            }
          ]
        }
        """;

    [Fact]
    public void Imports_folders_auth_inheritance_scripts_bodies_and_examples()
    {
        var result = Postman.Import(JsonNode.Parse(Collection)!);
        var collection = result.Collections.Single();

        Assert.Equal("Shop", collection.Name);
        Assert.Equal("https://shop.test", collection.Variables.Single().Value);
        var create = collection.Requests.Single(r => r.Name == "Create order");
        Assert.Equal("Orders", create.Folder);
        Assert.Equal("{{base}}/orders/{{id}}?dry=true", create.Url);
        Assert.Contains(create.QueryParams, q => q.Key == "debug" && !q.Enabled);
        Assert.Equal(AuthMode.Bearer, create.Auth.Mode); // inherited from the collection
        Assert.Contains("pm.test('global'", create.TestScript);
        Assert.Contains("pm.variables.set", create.PreRequestScript);
        Assert.Equal(201, create.Examples.Single().StatusCode);
        Assert.False(create.Headers.Single(h => h.Key == "X-Off").Enabled);

        var upload = collection.Requests.Single(r => r.Name == "Upload");
        Assert.Equal(AuthMode.Basic, upload.Auth.Mode);
        Assert.True(upload.Body.FormFields[0].IsFile);

        Assert.Equal(RequestKind.GraphQl, collection.Requests.Single(r => r.Name == "GraphQL").Kind);
    }

    [Fact]
    public void Export_then_import_round_trips()
    {
        var original = Postman.Import(JsonNode.Parse(Collection)!).Collections.Single();
        original.Requests[0].Assertions.Add(new Assertion { Source = ValueSource.Status, Expected = "201" });

        var exported = Postman.Export(original);
        var reimported = Postman.Import(JsonNode.Parse(exported)!).Collections.Single();

        Assert.Equal(original.Requests.Count, reimported.Requests.Count);
        var create = reimported.Requests.Single(r => r.Name == "Create order");
        Assert.Equal("Orders", create.Folder);
        Assert.Equal(BodyMode.Json, create.Body.Mode);
        Assert.Contains("pm.response.to.have.status(201)", create.TestScript);
        Assert.Equal(RequestKind.GraphQl, reimported.Requests.Single(r => r.Name == "GraphQL").Kind);
    }

    [Fact]
    public void Imports_environment_with_secrets()
    {
        var env = Postman.ImportEnvironment(JsonNode.Parse("""
            {"name":"Staging","values":[{"key":"base","value":"https://s","enabled":true},{"key":"pw","value":"x","type":"secret","enabled":true}],"_postman_variable_scope":"environment"}
            """)!);

        Assert.Equal("Staging", env.Name);
        Assert.True(env.Variables.Single(v => v.Key == "pw").IsSecret);
        Assert.Contains("\"value\": \"\"", Postman.ExportEnvironment(env)); // secrets are not exported
    }
}

public class OtherFormatTests
{
    [Fact]
    public void Http_file_import_and_export()
    {
        const string http = """
            @host = https://api.test
            ### List users
            GET {{host}}/users?limit=10 HTTP/1.1
            Accept: application/json

            ###
            # @name createUser
            POST {{host}}/users
            Content-Type: application/json

            {"name": "Ann"}

            > {% client.test("ok", () => {}); %}
            """;

        var collection = HttpFile.Import(http).Collections.Single();

        Assert.Equal("https://api.test", collection.Variables.Single().Value);
        Assert.Equal(["List users", "createUser"], collection.Requests.Select(r => r.Name));
        Assert.Equal("10", collection.Requests[0].QueryParams.Single().Value);
        Assert.Equal("""{"name": "Ann"}""", collection.Requests[1].Body.Content);

        var exported = HttpFile.Export(collection);
        Assert.Contains("POST {{host}}/users HTTP/1.1", exported);
        Assert.Equal(2, HttpFile.Import(exported).Collections.Single().Requests.Count);
    }

    [Fact]
    public void Har_import_skips_static_assets()
    {
        var har = JsonNode.Parse("""
            {"log":{"entries":[
              {"request":{"method":"POST","url":"https://a.test/api/login","headers":[{"name":":authority","value":"a.test"},{"name":"Content-Type","value":"application/json"}],
                          "postData":{"mimeType":"application/json","text":"{\"u\":1}"}},
               "response":{"status":200,"content":{"mimeType":"application/json","text":"{\"ok\":true}"}}},
              {"request":{"method":"GET","url":"https://a.test/logo.png","headers":[]},"response":{"status":200,"content":{}}}
            ]}}
            """)!;

        var request = Har.Import(har).Collections.Single().Requests.Single();

        Assert.Equal(HttpVerb.Post, request.Method);
        Assert.Equal(BodyMode.Json, request.Body.Mode);
        Assert.DoesNotContain(request.Headers, h => h.Key.StartsWith(':'));
        Assert.Equal(200, request.Examples.Single().StatusCode);
    }

    [Fact]
    public void Har_and_http_file_imports_decode_form_bodies()
    {
        var har = JsonNode.Parse("""
            {"log":{"entries":[
              {"request":{"method":"POST","url":"https://a.test/api/login","headers":[{"name":"Content-Type","value":"application/x-www-form-urlencoded"}],
                          "postData":{"mimeType":"application/x-www-form-urlencoded","text":"user=ann%40x.io&note=a+b%26c"}},
               "response":{"status":200,"content":{}}}
            ]}}
            """)!;
        var fromHar = Har.Import(har).Collections.Single().Requests.Single();
        Assert.Equal(BodyMode.FormUrlEncoded, fromHar.Body.Mode);
        Assert.Equal("ann@x.io", fromHar.Body.FormFields.Single(f => f.Key == "user").Value);
        Assert.Equal("a b&c", fromHar.Body.FormFields.Single(f => f.Key == "note").Value);

        const string http = """
            POST https://a.test/login
            Content-Type: application/x-www-form-urlencoded

            user=ann%40x.io&note=a+b%26c
            """;
        var fromHttp = HttpFile.Import(http).Collections.Single().Requests.Single();
        Assert.Equal(BodyMode.FormUrlEncoded, fromHttp.Body.Mode);
        Assert.Equal("ann@x.io", fromHttp.Body.FormFields.Single(f => f.Key == "user").Value);
        Assert.Equal("a b&c", fromHttp.Body.FormFields.Single(f => f.Key == "note").Value);
    }

    [Fact]
    public void Insomnia_graphql_body_that_is_not_json_is_kept_as_the_query()
    {
        var export = JsonNode.Parse("""
            {"_type":"export","resources":[
              {"_id":"wrk_1","_type":"workspace","name":"GQL"},
              {"_id":"req_1","_type":"request","parentId":"wrk_1","name":"Raw","method":"POST","url":"https://g.test/graphql",
               "body":{"mimeType":"application/graphql","text":"query { me { id } }"}},
              {"_id":"req_2","_type":"request","parentId":"wrk_1","name":"Empty","method":"POST","url":"https://g.test/graphql",
               "body":{"mimeType":"application/graphql","text":""}},
              {"_id":"req_3","_type":"request","parentId":"wrk_1","name":"Envelope","method":"POST","url":"https://g.test/graphql",
               "body":{"mimeType":"application/graphql","text":"{\"query\":\"query { me { id } }\",\"variables\":{\"a\":1}}"}}
            ]}
            """)!;

        var requests = Insomnia.Import(export).Collections.Single().Requests;

        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.Equal(RequestKind.GraphQl, r.Kind));
        Assert.Equal("query { me { id } }", requests[0].Protocol.GraphQl.Query);
        Assert.Equal("", requests[1].Protocol.GraphQl.Query);
        Assert.Equal("query { me { id } }", requests[2].Protocol.GraphQl.Query);
        Assert.Equal("""{"a":1}""", requests[2].Protocol.GraphQl.Variables);
    }

    [Fact]
    public void Http_file_export_tolerates_non_json_graphql_variables()
    {
        var collection = new RequestCollection { Name = "G" };
        collection.Requests.Add(new ApiRequest
        {
            Name = "Me", Kind = RequestKind.GraphQl, Url = "https://g.test/graphql",
            Protocol = new ProtocolSettings { GraphQl = new GraphQlSettings { Query = "query { me { id } }", Variables = "{{vars}}" } }
        });

        var exported = HttpFile.Export(collection);

        Assert.Contains("\"variables\":\"{{vars}}\"", exported);
    }

    [Fact]
    public void Insomnia_import_with_folders_templates_and_environment()
    {
        var export = JsonNode.Parse("""
            {"_type":"export","resources":[
              {"_id":"wrk_1","_type":"workspace","name":"My API"},
              {"_id":"fld_1","_type":"request_group","parentId":"wrk_1","name":"Users"},
              {"_id":"req_1","_type":"request","parentId":"fld_1","name":"Get user","method":"GET","url":"{{ _.base }}/users/1",
               "headers":[{"name":"X-A","value":"{{ _.a }}"}],"authentication":{"type":"bearer","token":"{{ _.token }}"},"body":{}},
              {"_id":"env_1","_type":"environment","parentId":"wrk_1","name":"Base","data":{"base":"https://x","a":1}}
            ]}
            """)!;

        var result = Insomnia.Import(export);
        var request = result.Collections.Single().Requests.Single();

        Assert.Equal("Users", request.Folder);
        Assert.Equal("{{base}}/users/1", request.Url);
        Assert.Equal("{{a}}", request.Headers.Single().Value);
        Assert.Equal("{{token}}", request.Auth.Token);
        Assert.Equal("1", result.Environments.Single().Variables.Single(v => v.Key == "a").Value);
    }

    [Fact]
    public void Dispatch_folder_round_trip_keeps_everything_and_prunes_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dispatch-folder-" + Guid.NewGuid().ToString("N"));
        try
        {
            var collection = new RequestCollection { Name = "Payments", Variables = [new("base", "https://p"), new("secret", "shh") { IsSecret = true }] };
            collection.Requests.Add(new ApiRequest
            {
                Name = "Charge card", Folder = "Cards/Visa", Method = HttpVerb.Post, Url = "{{base}}/charge", SortOrder = 0,
                Settings = new RequestSettings { FollowRedirects = false },
                Assertions = [new Assertion { Source = ValueSource.Status, Expected = "201" }]
            });
            collection.Requests.Add(new ApiRequest { Name = "Ping", Url = "{{base}}/ping", SortOrder = 1 });

            DispatchFormat.ExportFolder(collection, dir);
            var ping = File.ReadAllText(Directory.GetFiles(dir, "*ping*", SearchOption.AllDirectories).Single());
            var imported = DispatchFormat.ImportFolder(dir).Collections.Single();

            Assert.True(File.Exists(Path.Combine(dir, "cards", "visa", "000-charge-card.request.json")));
            Assert.DoesNotContain("settings", ping); // defaults are pruned
            Assert.Equal("", imported.Variables.Single(v => v.Key == "secret").Value); // secrets are not written
            var charge = imported.Requests.Single(r => r.Name == "Charge card");
            Assert.Equal("Cards/Visa", charge.Folder);
            Assert.False(charge.Settings.FollowRedirects);
            Assert.Equal("201", charge.Assertions.Single().Expected);

            // Removing a request removes its file on the next export.
            collection.Requests.RemoveAt(1);
            DispatchFormat.ExportFolder(collection, dir);
            Assert.Empty(Directory.GetFiles(dir, "*ping*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class OpenApiTests
{
    private const string Spec = """
        openapi: 3.0.3
        info:
          title: Pet Store
        servers:
          - url: https://{env}.pets.test/v1
            variables:
              env: { default: api }
        components:
          securitySchemes:
            bearerAuth: { type: http, scheme: bearer }
          schemas:
            Pet:
              type: object
              required: [id, name]
              properties:
                id: { type: integer, readOnly: true }
                name: { type: string, example: Rex }
                tag: { type: string, nullable: true }
                born: { type: string, format: date }
        security:
          - bearerAuth: []
        paths:
          /pets/{petId}:
            parameters:
              - { name: petId, in: path, required: true, schema: { type: integer, example: 7 } }
            get:
              tags: [pets]
              summary: Get a pet
              parameters:
                - { name: fields, in: query, schema: { type: string } }
              responses:
                '200':
                  description: OK
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Pet' }
                '404': { description: Not found }
          /pets:
            post:
              tags: [pets]
              operationId: createPet
              requestBody:
                content:
                  application/json:
                    schema: { $ref: '#/components/schemas/Pet' }
              responses:
                '201': { description: Created }
        """;

    [Fact]
    public void Imports_operations_with_variables_examples_auth_and_assertions()
    {
        var result = Importer.ImportText(Spec, "pets", "/specs/pets.yaml");
        var collection = result.Collections.Single();

        Assert.Equal("Pet Store", collection.Name);
        Assert.Equal("https://{{env}}.pets.test/v1", collection.Variables.Single(v => v.Key == "baseUrl").Value);
        Assert.Equal("7", collection.Variables.Single(v => v.Key == "petId").Value);
        Assert.Equal("/specs/pets.yaml", collection.SpecLocation);

        var get = collection.Requests.Single(r => r.Name == "Get a pet");
        Assert.Equal("{{baseUrl}}/pets/{{petId}}", get.Url.Split('?')[0]);
        Assert.Equal("pets", get.Folder);
        Assert.Equal(AuthMode.Bearer, get.Auth.Mode);
        Assert.Contains(get.Assertions, a => a is { Source: ValueSource.Status, Expected: "200" });
        Assert.Contains(get.Assertions, a => a.Source == ValueSource.Contract);

        var create = collection.Requests.Single(r => r.Name == "createPet");
        var body = JsonNode.Parse(create.Body.Content)!;
        Assert.Equal("Rex", body["name"]!.GetValue<string>());
        Assert.Null(body["id"]); // readOnly properties are left out of request examples
    }

    [Fact]
    public async Task Contract_validator_checks_path_status_and_schema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pets-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, Spec);
        try
        {
            using var pool = new HttpClientPool();
            var validator = new OpenApiContractValidator(pool);
            var request = new ApiRequest { Method = HttpVerb.Get };
            ApiResponse Response(int status, string body, string url = "https://api.pets.test/v1/pets/7") =>
                new() { StatusCode = status, Body = body, ContentType = "application/json", EffectiveUrl = url };

            Assert.Empty(await validator.ValidateAsync(path, request, Response(200, """{"id":7,"name":"Rex","tag":null}"""), CancellationToken.None));

            var badBody = await validator.ValidateAsync(path, request, Response(200, """{"id":"7"}"""), CancellationToken.None);
            Assert.Contains(badBody, e => e.Contains("missing required property 'name'"));
            Assert.Contains(badBody, e => e.Contains("$.id") && e.Contains("integer"));

            var undocumented = await validator.ValidateAsync(path, request, Response(500, "{}"), CancellationToken.None);
            Assert.Contains("Status 500 is not documented", undocumented.Single());

            var unknownPath = await validator.ValidateAsync(path, request, Response(200, "{}", "https://api.pets.test/v1/owners"), CancellationToken.None);
            Assert.Contains("No operation", unknownPath.Single());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Swagger2_json_import()
    {
        const string swagger = """
            {"swagger":"2.0","info":{"title":"Old"},"host":"old.test","basePath":"/api","schemes":["http"],
             "securityDefinitions":{"key":{"type":"apiKey","name":"X-Key","in":"header"}},"security":[{"key":[]}],
             "paths":{"/items":{"post":{"parameters":[{"in":"body","name":"b","schema":{"type":"object","properties":{"n":{"type":"integer"}}}}],
             "responses":{"200":{"description":"ok"}}}}}}
            """;

        var collection = Importer.ImportText(swagger).Collections.Single();

        Assert.Equal("http://old.test/api", collection.Variables.Single(v => v.Key == "baseUrl").Value);
        var request = collection.Requests.Single();
        Assert.Equal(AuthMode.ApiKey, request.Auth.Mode);
        Assert.Equal("X-Key", request.Auth.ApiKeyName);
        Assert.Equal(0, JsonNode.Parse(request.Body.Content)!["n"]!.GetValue<int>());
    }

    [Fact]
    public void Importer_detects_formats()
    {
        Assert.Equal("cURL", Importer.ImportText("curl https://a.test\ncurl -X POST https://b.test -d x=1").Format);
        Assert.Equal(2, Importer.ImportText("curl https://a.test\ncurl -X POST https://b.test -d x=1").RequestCount);
        Assert.Equal(".proto", Importer.ImportText("syntax = \"proto3\"; package p; service S { rpc M (A) returns (A); } message A { string x = 1; }").Format);
        Assert.Equal("Postman", Importer.ImportText("""{"info":{"name":"x","schema":"https://schema.getpostman.com/json/collection/v2.1.0/"},"item":[]}""").Format);
        Assert.Throws<FormatException>(() => Importer.ImportText("just some text"));
    }
}
