using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dispatch.Application.Auth;
using Dispatch.Application.Requests;
using Dispatch.Application.Testing;
using Dispatch.Application.Variables;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Tests;

public class JsonPathTests
{
    private static readonly JsonNode Doc = JsonNode.Parse("""
        {
          "store": {
            "books": [
              { "title": "A", "price": 8.95, "tags": ["x"] },
              { "title": "B", "price": 12.99, "isbn": "123" },
              { "title": "C", "price": 22.5 }
            ],
            "owner": { "name": "Ann" }
          },
          "count": 3
        }
        """)!;

    [Theory]
    [InlineData("$.count", "3")]
    [InlineData("store.owner.name", "Ann")]
    [InlineData("$.store.books[0].title", "A")]
    [InlineData("$.store.books[-1].title", "C")]
    [InlineData("$['store']['owner']['name']", "Ann")]
    [InlineData("$.store.books.length()", "3")]
    [InlineData("$.store.books[?(@.price > 10)].title", "B")]
    [InlineData("$.store.books[?(@.isbn)].title", "B")]
    [InlineData("$.store.books[?(@.title == 'C')].price", "22.5")]
    [InlineData("$..name", "Ann")]
    public void Selects_first_match(string path, string expected) =>
        Assert.Equal(expected, JsonPath.SelectText(Doc, path));

    [Fact]
    public void Wildcards_slices_and_unions_return_all_matches()
    {
        Assert.Equal(3, JsonPath.Select(Doc, "$.store.books[*].title").Count);
        Assert.Equal(["A", "B"], JsonPath.Select(Doc, "$.store.books[0:2].title").Select(JsonPath.ToText));
        Assert.Equal(["A", "C"], JsonPath.Select(Doc, "$.store.books[0,2].title").Select(JsonPath.ToText));
        Assert.Equal(3, JsonPath.Select(Doc, "$..price").Count);
    }

    [Fact]
    public void Missing_path_returns_nothing() => Assert.Empty(JsonPath.Select(Doc, "$.store.nope.x"));
}

public class JsonSchemaValidatorTests
{
    private const string Schema = """
        {
          "type": "object",
          "required": ["id", "email"],
          "properties": {
            "id": { "type": "integer", "minimum": 1 },
            "email": { "type": "string", "format": "email" },
            "role": { "enum": ["admin", "user"] },
            "tags": { "type": "array", "items": { "type": "string" }, "maxItems": 2 },
            "address": { "$ref": "#/definitions/address" }
          },
          "additionalProperties": false,
          "definitions": {
            "address": { "type": "object", "required": ["city"], "properties": { "city": { "type": "string" } } }
          }
        }
        """;

    [Fact]
    public void Valid_document_has_no_errors() =>
        Assert.Empty(JsonSchemaValidator.Validate("""{"id":1,"email":"a@b.io","role":"admin","tags":["x"],"address":{"city":"Yerevan"}}""", Schema));

    [Fact]
    public void Reports_each_violation()
    {
        var errors = JsonSchemaValidator.Validate("""{"id":0,"email":"nope","role":"root","tags":[1,"a","b"],"address":{},"extra":1}""", Schema);

        Assert.Contains(errors, e => e.Contains("$.id") && e.Contains("minimum"));
        Assert.Contains(errors, e => e.Contains("$.email") && e.Contains("email"));
        Assert.Contains(errors, e => e.Contains("$.role"));
        Assert.Contains(errors, e => e.Contains("$.tags[0]"));
        Assert.Contains(errors, e => e.Contains("at most 2 items"));
        Assert.Contains(errors, e => e.Contains("'city'"));
        Assert.Contains(errors, e => e.Contains("unexpected property 'extra'"));
    }

    [Fact]
    public void OpenApi_nullable_allows_null() =>
        Assert.Empty(JsonSchemaValidator.Validate("""{"a":null}""",
            """{"type":"object","properties":{"a":{"type":"string","nullable":true}}}"""));

    [Fact]
    public void OneOf_requires_exactly_one_match() =>
        Assert.NotEmpty(JsonSchemaValidator.Validate("5", """{"oneOf":[{"type":"number"},{"type":"integer"}]}"""));
}

public class AssertionTests
{
    private static readonly ApiResponse Response = new()
    {
        StatusCode = 201,
        ReasonPhrase = "Created",
        Elapsed = TimeSpan.FromMilliseconds(120),
        ContentType = "application/json",
        Body = """{"id":42,"user":{"name":"Ann","roles":["a","b"]},"token":"abc.def"}""",
        Headers = [new ResponseHeader("Content-Type", "application/json"), new ResponseHeader("X-Trace", "t-1")]
    };

    private static Task<IReadOnlyList<TestResult>> Run(params Assertion[] assertions) =>
        new AssertionEvaluator().EvaluateAsync(new ApiRequest { Assertions = [.. assertions] }, Response, s => s, null,
            CancellationToken.None);

    [Fact]
    public async Task Passing_assertions()
    {
        var results = await Run(
            new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "201" },
            new Assertion { Source = ValueSource.Status, Operator = AssertionOperator.LessThan, Expected = "300" },
            new Assertion { Source = ValueSource.Header, Path = "x-trace", Operator = AssertionOperator.Equals, Expected = "t-1" },
            new Assertion { Source = ValueSource.JsonPath, Path = "$.user.name", Operator = AssertionOperator.Equals, Expected = "Ann" },
            new Assertion { Source = ValueSource.JsonPath, Path = "$.user.roles", Operator = AssertionOperator.LengthEquals, Expected = "2" },
            new Assertion { Source = ValueSource.JsonPath, Path = "$.id", Operator = AssertionOperator.IsType, Expected = "number" },
            new Assertion { Source = ValueSource.JsonPath, Path = "$.user", Operator = AssertionOperator.Equals, Expected = """{ "roles": ["a","b"], "name": "Ann" }""" },
            new Assertion { Source = ValueSource.JsonPath, Path = "$.missing", Operator = AssertionOperator.NotExists },
            new Assertion { Source = ValueSource.Regex, Path = "\"token\":\"([^\"]+)\"", Operator = AssertionOperator.Matches, Expected = "^abc" },
            new Assertion { Source = ValueSource.ResponseTime, Operator = AssertionOperator.LessOrEqual, Expected = "500" },
            new Assertion { Source = ValueSource.JsonSchema, Path = """{"type":"object","required":["id"]}""" });

        Assert.All(results, r => Assert.True(r.Passed, $"{r.Name}: {r.Message}"));
    }

    [Fact]
    public async Task Failing_assertion_reports_actual_value()
    {
        var result = (await Run(new Assertion
            { Source = ValueSource.JsonPath, Path = "$.id", Operator = AssertionOperator.Equals, Expected = "7" })).Single();

        Assert.False(result.Passed);
        Assert.Equal("42", result.Actual);
    }

    [Fact]
    public void XPath_binds_document_namespace_prefixes()
    {
        const string soap = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><m:Result xmlns:m="urn:calc">5</m:Result></soap:Body>
            </soap:Envelope>
            """;
        Assert.Equal(["5"], ResponseValues.SelectXPath(soap, "//soap:Body/m:Result"));
        Assert.Equal(["5"], ResponseValues.SelectXPath(soap, "//*[local-name()='Result']"));
    }

    [Fact]
    public void Extraction_rules_produce_variables()
    {
        var request = new ApiRequest
        {
            Extractions =
            [
                new ExtractionRule { Variable = "userName", Source = ValueSource.JsonPath, Path = "$.user.name" },
                new ExtractionRule { Variable = "trace", Source = ValueSource.Header, Path = "X-Trace", Scope = VariableScope.Runtime },
                new ExtractionRule { Variable = "none", Source = ValueSource.JsonPath, Path = "$.nope" }
            ]
        };

        var (values, failures) = AssertionEvaluator.Extract(request, Response, s => s);

        Assert.Equal("Ann", values["userName"].Value);
        Assert.Equal(VariableScope.Runtime, values["trace"].Scope);
        Assert.Single(failures);
    }
}

public class VariableTests
{
    [Fact]
    public void Dynamic_variables_generate_values()
    {
        var resolved = VariableResolver.Resolve("{{$guid}}|{{$timestamp}}|{{$randomInt}}", new Dictionary<string, string>());
        var parts = resolved.Split('|');

        Assert.True(Guid.TryParse(parts[0], out _));
        Assert.True(long.TryParse(parts[1], out _));
        Assert.True(int.TryParse(parts[2], out _));
        Assert.Empty(VariableResolver.FindUnresolved("{{$guid}}", new Dictionary<string, string>()));
    }

    [Fact]
    public void Context_layers_by_precedence_and_tracks_environment_updates()
    {
        var env = new ApiEnvironment { Variables = [new("host", "env"), new("a", "1")] };
        var ctx = VariableContext.For(env, [new KeyValueItem("host", "collection"), new KeyValueItem("c", "3")]);
        ctx.Data["a"] = "data";

        Assert.Equal("env", ctx.Get("host"));
        Assert.Equal("data", ctx.Get("a"));
        Assert.Equal("3", ctx.Get("c"));

        ctx.Set("token", "t", VariableScope.Environment);
        ctx.Set("tmp", "x", VariableScope.Runtime);
        Assert.Equal("t", ctx.EnvironmentUpdates["token"]);
        Assert.False(ctx.EnvironmentUpdates.ContainsKey("tmp"));
        Assert.Equal("x", ctx.Merged()["tmp"]);
    }

    [Fact]
    public void Request_resolver_resolves_every_protocol_field_but_not_scripts()
    {
        var request = new ApiRequest
        {
            Kind = RequestKind.Grpc,
            Url = "{{host}}",
            PreRequestScript = "pm.variables.get('{{host}}')",
            Protocol = new ProtocolSettings { Grpc = new GrpcSettings { Service = "{{svc}}", Message = """{"id":"{{id}}"}""" } },
            Assertions = [new Assertion { Expected = "{{id}}" }]
        };

        var resolved = RequestResolver.Resolve(request, new Dictionary<string, string>
        {
            ["host"] = "localhost:5001",
            ["svc"] = "pkg.Users",
            ["id"] = "7"
        });

        Assert.Equal(request.Id, resolved.Id);
        Assert.Equal("localhost:5001", resolved.Url);
        Assert.Equal("pkg.Users", resolved.Protocol.Grpc.Service);
        Assert.Equal("""{"id":"7"}""", resolved.Protocol.Grpc.Message);
        Assert.Equal("7", resolved.Assertions[0].Expected);
        Assert.Contains("{{host}}", resolved.PreRequestScript);
        Assert.Equal(RequestKind.Grpc, resolved.Kind);
    }
}

public class AwsSigV4Tests
{
    [Fact]
    public async Task Matches_aws_documented_example()
    {
        // https://docs.aws.amazon.com/IAM/latest/UserGuide/create-signed-request.html (IAM ListUsers example)
        var request = new HttpRequestMessage(HttpMethod.Get, "https://iam.amazonaws.com/?Action=ListUsers&Version=2010-05-08")
        {
            Content = new StringContent("", Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded")
        {
            CharSet = "utf-8"
        };

        await AwsSigV4Signer.SignAsync(request, new AuthSettings
        {
            AwsAccessKey = "AKIDEXAMPLE",
            AwsSecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
            AwsRegion = "us-east-1",
            AwsService = "iam"
        }, new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero));

        var authorization = string.Join("", request.Headers.GetValues("Authorization"));
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/iam/aws4_request, " +
            "SignedHeaders=content-type;host;x-amz-date, " +
            "Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7",
            authorization);
    }
}

public class CookieJarTests
{
    [Fact]
    public void Stores_and_sends_cookies_by_domain_and_round_trips_through_json()
    {
        var jar = new CookieJar();
        jar.Store(new Uri("https://api.example.com/login"), ["session=abc; Path=/", "theme=dark; Path=/"]);

        Assert.Equal("session=abc; theme=dark", jar.GetCookieHeader(new Uri("https://api.example.com/users")));
        Assert.Null(jar.GetCookieHeader(new Uri("https://other.example.org/")));

        var copy = new CookieJar();
        copy.Import(jar.Export());
        Assert.Equal(2, copy.GetAll().Count);

        copy.Delete(".api.example.com", "/", "theme");
        copy.Delete("api.example.com", "/", "theme");
        Assert.Single(copy.GetAll());
    }
}

public class HttpProtocolExecutorTests
{
    private sealed class CapturingExecutor : Dispatch.Application.Abstractions.IRequestExecutor
    {
        public HttpRequestMessage? Last { get; private set; }

        public Task<ApiResponse> ExecuteAsync(HttpRequestMessage request, RequestSettings settings, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new ApiResponse
            {
                StatusCode = 200,
                Headers = [new ResponseHeader("Set-Cookie", "sid=1; Path=/")],
                EffectiveUrl = request.RequestUri!.ToString()
            });
        }
    }

    [Fact]
    public async Task Applies_cookie_jar_and_captures_raw_request()
    {
        var jar = new CookieJar();
        var inner = new CapturingExecutor();
        var executor = new HttpProtocolExecutor(new RequestMessageBuilder(), inner, jar);
        var request = new ApiRequest
        {
            Method = HttpVerb.Post,
            Url = "https://t.test/a?x=1",
            Body = new RequestBody { Mode = BodyMode.Json, Content = """{"a":1}""" }
        };

        var first = await executor.SendAsync(request, CancellationToken.None);
        await executor.SendAsync(request, CancellationToken.None);

        Assert.StartsWith("POST /a?x=1 HTTP/1.1", first.RawRequest);
        Assert.Contains("""{"a":1}""", first.RawRequest);
        Assert.Equal("sid=1", string.Join("", inner.Last!.Headers.GetValues("Cookie")));
    }

    [Fact]
    public async Task Multipart_body_includes_files_and_fields()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "hello");
        try
        {
            using var message = new RequestMessageBuilder().Build(new ApiRequest
            {
                Method = HttpVerb.Post,
                Url = "https://t.test/upload",
                Body = new RequestBody
                {
                    Mode = BodyMode.Multipart,
                    FormFields = [new("name", "doc"), new("file", file) { IsFile = true }]
                }
            }, new Dictionary<string, string>());

            var body = await message.Content!.ReadAsStringAsync();
            Assert.Contains("multipart/form-data", message.Content.Headers.ContentType!.ToString());
            Assert.Contains("hello", body);
            Assert.Contains("name=name", body);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Real_http_round_trip_reports_timings()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes("""{"ok":true}""");
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        using var pool = new HttpClientPool();
        var executor = new HttpRequestExecutor(pool);
        var response = await executor.ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/"),
            new RequestSettings { TimeoutMs = 5000 }, CancellationToken.None);
        await serve;

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("""{"ok":true}""", response.Body);
        Assert.NotNull(response.Timings?.Connect);
    }

    internal static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

public sealed class SchemaUpgradeTests
{
    [Fact]
    public async Task Adds_missing_columns_to_a_database_from_an_older_version()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dispatch-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            // The v0.1 schema: no Kind / Protocol / Settings / ... columns. (EF stores GUIDs as upper-case text.)
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE "Collections" ("Id" TEXT NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "SortOrder" INTEGER NOT NULL, "CreatedAt" INTEGER NOT NULL);
                    CREATE TABLE "Environments" ("Id" TEXT NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "Variables" TEXT NOT NULL);
                    CREATE TABLE "Settings" ("Key" TEXT NOT NULL PRIMARY KEY, "Value" TEXT NULL);
                    CREATE TABLE "History" ("Id" TEXT NOT NULL PRIMARY KEY, "Timestamp" INTEGER NOT NULL, "Method" TEXT NOT NULL, "Url" TEXT NOT NULL, "StatusCode" INTEGER NULL, "ElapsedMs" REAL NOT NULL, "Request" TEXT NOT NULL);
                    CREATE TABLE "Requests" ("Id" TEXT NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "Method" TEXT NOT NULL, "Url" TEXT NOT NULL, "QueryParams" TEXT NOT NULL, "Headers" TEXT NOT NULL, "Body" TEXT NOT NULL, "Auth" TEXT NOT NULL, "CollectionId" TEXT NULL, "SortOrder" INTEGER NOT NULL, "UpdatedAt" INTEGER NOT NULL);
                    INSERT INTO "Settings" VALUES ('seeded', '1');
                    INSERT INTO "Collections" VALUES ('0B1C3A5E-0000-0000-0000-000000000001', 'Old', 0, 0);
                    INSERT INTO "Requests" VALUES ('0B1C3A5E-0000-0000-0000-000000000002', 'Old request', 'Get', 'https://x', '[]', '[]', '{"mode":"None","content":""}', '{"mode":"None"}', '0B1C3A5E-0000-0000-0000-000000000001', 0, 0);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var factory = new PooledlessFactory(path);
            await new DatabaseInitializer(factory).InitializeAsync();

            var request = (await new CollectionRepository(factory).GetAllAsync()).Single().Requests.Single();
            Assert.Equal("Old request", request.Name);
            Assert.Equal(RequestKind.Http, request.Kind);
            Assert.Empty(request.Assertions);
            Assert.True(request.Settings.FollowRedirects);

            request.Kind = RequestKind.Grpc;
            request.Assertions.Add(new Assertion());
            await new CollectionRepository(factory).SaveRequestAsync(request);
            var reloaded = (await new CollectionRepository(factory).GetAllAsync()).Single().Requests.Single();
            Assert.Equal(RequestKind.Grpc, reloaded.Kind);
            Assert.Single(reloaded.Assertions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class PooledlessFactory(string path) : IDbContextFactory<DispatchDbContext>
    {
        public DispatchDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<DispatchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}

