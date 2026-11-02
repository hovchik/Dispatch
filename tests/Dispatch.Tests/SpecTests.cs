using System.Text.Json.Nodes;
using Dispatch.Application.Impact;
using Dispatch.Application.Interop;
using Dispatch.Application.Spec;
using Dispatch.Domain;
using Dispatch.Infrastructure.Interop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Tests;

internal static class SpecScenario
{
    public const string V1 = """
        openapi: 3.0.0
        info: { title: Pets, version: "1.0" }
        servers: [{ url: https://api.example.com/v1 }]
        paths:
          /pets:
            get:
              operationId: listPets
              parameters:
                - { name: limit, in: query, schema: { type: integer } }
                - { name: status, in: query, required: true, schema: { type: string, enum: [available, sold] } }
              responses:
                "200": { description: ok, content: { application/json: { schema: { type: array, items: { $ref: "#/components/schemas/Pet" } } } } }
                "400": { description: bad }
            post:
              operationId: createPet
              requestBody:
                required: true
                content: { application/json: { schema: { $ref: "#/components/schemas/NewPet" } } }
              responses:
                "201": { description: created, content: { application/json: { schema: { $ref: "#/components/schemas/Pet" } } } }
                "422": { description: invalid }
          /pets/{id}:
            get:
              operationId: getPet
              parameters: [{ name: id, in: path, required: true, schema: { type: integer } }]
              responses:
                "200": { description: ok, content: { application/json: { schema: { $ref: "#/components/schemas/Pet" } } } }
                "404": { description: missing }
            delete:
              operationId: deletePet
              parameters: [{ name: id, in: path, required: true, schema: { type: integer } }]
              responses: { "204": { description: gone } }
        components:
          schemas:
            NewPet:
              type: object
              required: [name, kind]
              properties: { name: { type: string }, kind: { type: string }, age: { type: integer } }
            Pet:
              allOf:
                - $ref: "#/components/schemas/NewPet"
                - type: object
                  required: [id]
                  properties: { id: { type: integer }, owner: { type: object, properties: { email: { type: string } } } }
        """;

    /// <summary>v2: delete removed, new required query param, limit retyped, enum narrowed, id renamed to petId, 201 → 200, pets now need auth.</summary>
    public const string V2 = """
        openapi: 3.0.0
        info: { title: Pets, version: "2.0" }
        servers: [{ url: https://api.example.com/v1 }]
        components:
          securitySchemes: { bearer: { type: http, scheme: bearer } }
          schemas:
            NewPet:
              type: object
              required: [name, kind]
              properties: { name: { type: string }, kind: { type: string }, age: { type: integer }, color: { type: string } }
            Pet:
              allOf:
                - $ref: "#/components/schemas/NewPet"
                - type: object
                  required: [petId]
                  properties: { petId: { type: integer }, owner: { type: object, properties: { email: { type: string } } } }
        paths:
          /pets:
            get:
              operationId: listPets
              security: [{ bearer: [] }]
              parameters:
                - { name: limit, in: query, schema: { type: string } }
                - { name: sort, in: query, required: true, schema: { type: string } }
                - { name: status, in: query, required: true, schema: { type: string, enum: [available] } }
              responses:
                "200": { description: ok, content: { application/json: { schema: { type: array, items: { $ref: "#/components/schemas/Pet" } } } } }
                "400": { description: bad }
            post:
              operationId: createPet
              requestBody:
                required: true
                content: { application/json: { schema: { $ref: "#/components/schemas/NewPet" } } }
              responses:
                "200": { description: created, content: { application/json: { schema: { $ref: "#/components/schemas/Pet" } } } }
                "422": { description: invalid }
          /pets/{id}:
            get:
              operationId: getPet
              parameters: [{ name: id, in: path, required: true, schema: { type: integer } }]
              responses:
                "200": { description: ok, content: { application/json: { schema: { $ref: "#/components/schemas/Pet" } } } }
                "404": { description: missing }
        """;

    public const string Har = """
        {"log":{"entries":[
         {"startedDateTime":"2026-01-01T00:00:00Z","request":{"method":"GET","url":"https://api.example.com/v1/pets?status=available&limit=5","headers":[],"queryString":[{"name":"status","value":"available"},{"name":"limit","value":"5"}]},"response":{"status":200,"content":{"text":"[]"}}},
         {"startedDateTime":"2026-01-01T00:00:01Z","request":{"method":"GET","url":"https://api.example.com/v1/pets?status=sold","headers":[],"queryString":[{"name":"status","value":"sold"}]},"response":{"status":200,"content":{"text":"[]"}}},
         {"startedDateTime":"2026-01-01T00:00:02Z","request":{"method":"GET","url":"https://api.example.com/v1/pets","headers":[]},"response":{"status":400,"content":{"text":"{}"}}},
         {"startedDateTime":"2026-01-01T00:00:03Z","request":{"method":"POST","url":"https://api.example.com/v1/pets","headers":[],"postData":{"text":"{\"name\":\"Rex\"}"}},"response":{"status":422,"content":{"text":"{}"}}},
         {"startedDateTime":"2026-01-01T00:00:04Z","request":{"method":"GET","url":"https://api.example.com/v1/pets/7","headers":[]},"response":{"status":200,"content":{"text":"{}"}}},
         {"startedDateTime":"2026-01-01T00:00:05Z","request":{"method":"PUT","url":"https://api.example.com/v1/pets/7","headers":[]},"response":{"status":405,"content":{"text":"{}"}}},
         {"startedDateTime":"2026-01-01T00:00:06Z","request":{"method":"GET","url":"https://api.example.com/v1/owners","headers":[]},"response":{"status":200,"content":{"text":"{}"}}}
        ]}}
        """;

    public static SpecDocument Spec(string yaml) => SpecLoader.Parse(yaml);

    /// <summary>A collection that imports v1 (one request per operation) with the importer's status assertions.</summary>
    public static RequestCollection Collection() => Importer.ImportText(V1, "pets", "pets.yaml").Collections[0];
}

public class SpecDocumentTests
{
    [Fact]
    public void Parses_operations_parameters_bodies_and_responses()
    {
        var spec = SpecScenario.Spec(SpecScenario.V1);

        Assert.Equal("Pets", spec.Title);
        Assert.Equal(["/v1", ""], spec.BasePaths);
        Assert.Equal(4, spec.Operations.Count);
        var list = spec.Find("GET", "/pets")!;
        Assert.Equal(["query:limit", "query:status"], list.Parameters.Select(p => p.Key));
        Assert.Equal(["\"available\"", "\"sold\""], list.Parameters[1].Enum);
        Assert.Equal(["200", "400"], list.Responses.Select(r => r.Code));
        var create = spec.Find("POST", "/pets")!;
        Assert.True(create.RequestBodyRequired);
        Assert.Equal(["name", "kind"], SchemaShape.RequiredOf(spec.Root, create.RequestSchema));
        Assert.Equal("201", create.SuccessResponse!.Code);
    }

    [Fact]
    public void Matches_real_paths_to_templates_with_and_without_the_server_base_path()
    {
        var spec = SpecScenario.Spec(SpecScenario.V1);

        Assert.Equal("GET /pets/{id}", spec.Match("GET", "https://api.example.com/v1/pets/42").Operation!.Key);
        Assert.Equal("GET /pets/{id}", spec.Match("get", "/pets/42/").Operation!.Key);
        Assert.Equal("GET /pets/{id}", spec.Match("GET", "{{baseUrl}}/pets/{{id}}").Operation!.Key);
        Assert.Equal("42", spec.PathValues(spec.Find("GET", "/pets/{id}")!, "https://api.example.com/v1/pets/42")["id"]);
        // Documented path, undocumented method.
        var (operation, template) = spec.Match("PUT", "/v1/pets/42");
        Assert.Null(operation);
        Assert.Equal("/pets/{id}", template);
        Assert.Equal((null, null), spec.Match("GET", "/owners"));
        // Unknown method: any operation on the path, GET first.
        Assert.Equal("GET /pets", spec.Match(null, "/pets").Operation!.Key);
    }

    [Fact]
    public void Flattens_schemas_through_refs_allOf_and_arrays()
    {
        var spec = SpecScenario.Spec(SpecScenario.V1);
        var fields = SchemaShape.Flatten(spec.Root, spec.Find("GET", "/pets")!.SuccessResponse!.Schema);

        Assert.Equal("array", fields["$"].TypeText);
        Assert.True(fields["$[*].id"].Required);
        Assert.Equal("integer", fields["$[*].id"].TypeText);
        Assert.False(fields["$[*].age"].Required);
        Assert.Equal("string", fields["$[*].owner.email"].TypeText);
    }

    [Fact]
    public void Flattening_stops_on_recursive_schemas()
    {
        var root = JsonNode.Parse("""{"openapi":"3.0.0","paths":{},"components":{"schemas":{"Node":{"type":"object","properties":{"name":{"type":"string"},"children":{"type":"array","items":{"$ref":"#/components/schemas/Node"}}}}}}}""")!;
        var fields = SchemaShape.Flatten(root, root["components"]!["schemas"]!["Node"]);
        Assert.Contains("$.children[*].name", fields.Keys);
        Assert.True(fields.Count < 10);
    }

    [Fact]
    public void Reads_swagger_2_documents()
    {
        var spec = SpecLoader.Parse("""
            {"swagger":"2.0","info":{"title":"Old","version":"1"},"host":"h","basePath":"/api","schemes":["https"],
             "paths":{"/things":{"post":{"parameters":[{"name":"body","in":"body","required":true,"schema":{"type":"object","required":["a"],"properties":{"a":{"type":"string"}}}},
             {"name":"X-Trace","in":"header","type":"string"}],"responses":{"200":{"description":"ok","schema":{"type":"object","properties":{"id":{"type":"integer"}}}}}}}}}
            """);
        Assert.True(spec.IsSwagger2);
        Assert.Equal(["/api", ""], spec.BasePaths);
        var op = Assert.Single(spec.Operations);
        Assert.Equal(["header:X-Trace"], op.Parameters.Select(p => p.Key));
        Assert.Equal(["a"], SchemaShape.RequiredOf(spec.Root, op.RequestSchema));
        Assert.Equal("integer", SchemaShape.Flatten(spec.Root, op.SuccessResponse!.Schema)["$.id"].TypeText);
    }
}

public class SpecCoverageTests
{
    [Fact]
    public void Scores_har_traffic_on_every_metric()
    {
        var spec = SpecScenario.Spec(SpecScenario.V1);
        var samples = CoverageSamples.FromHar(JsonNode.Parse(SpecScenario.Har)!);
        var report = SpecCoverage.Analyze(spec, samples, "traffic.har");

        Assert.Equal(7, report.SampleCount);
        var operations = report[CoverageMetricId.Operations];
        Assert.Equal((3, 4), (operations.Covered, operations.Total));
        Assert.Contains(operations.Gaps, g => g.Operation == "DELETE /pets/{id}");

        var statuses = report[CoverageMetricId.StatusCodes];
        Assert.Equal((4, 7), (statuses.Covered, statuses.Total));
        Assert.Contains(statuses.Gaps, g => g is { Operation: "POST /pets", Detail: "201 never seen" });

        var parameters = report[CoverageMetricId.Parameters];
        Assert.Equal((2, 4), (parameters.Covered, parameters.Total)); // status varied (available/sold), limit present and absent
        Assert.Contains(parameters.Gaps, g => g.Operation == "GET /pets/{id}" && g.Detail.Contains("always '7'"));

        var required = report[CoverageMetricId.RequiredFields];
        Assert.Equal((2, 3), (required.Covered, required.Total)); // status omitted once, kind omitted; name never omitted
        Assert.Contains(required.Gaps, g => g.Detail.Contains("'name'"));

        var documented = report[CoverageMetricId.Documented];
        Assert.Equal((5, 7), (documented.Covered, documented.Total));
        Assert.Contains(report.Undocumented, g => g.Operation == "PUT /v1/pets/{id}" && g.Hint!.Contains("not for PUT"));
        Assert.Contains(report.Undocumented, g => g.Operation == "GET /v1/owners");

        Assert.InRange(report.Overall, 63, 65);
        Assert.False(report.Passes(80));
        Assert.True(report.Passes(60));
    }

    [Fact]
    public void Scores_a_collection_as_defined_using_asserted_statuses_and_examples()
    {
        var spec = SpecScenario.Spec(SpecScenario.V1);
        var collection = SpecScenario.Collection();
        var report = SpecCoverage.Analyze(spec, CoverageSamples.FromCollection(collection), collection.Name);

        Assert.Equal(100, report[CoverageMetricId.Operations].Percent);
        Assert.Equal(100, report[CoverageMetricId.Documented].Percent);
        Assert.Equal(0, report[CoverageMetricId.Parameters].Percent); // one value per request
        Assert.Contains(report[CoverageMetricId.StatusCodes].Gaps, g => g is { Operation: "GET /pets/{id}", Detail: "404 never seen" });
        Assert.DoesNotContain(report[CoverageMetricId.StatusCodes].Gaps, g => g.Detail == "201 never seen");
    }

    [Fact]
    public void Reads_run_json_reports_and_joins_the_collection_for_methods()
    {
        var runJson = JsonNode.Parse("""
            {"name":"Pets","totals":{"requests":2},"results":[
              {"name":"createPet","kind":"Http","url":"https://api.example.com/v1/pets","status":201},
              {"name":"getPet","kind":"Http","url":"https://api.example.com/v1/pets/3","status":404}]}
            """)!;
        Assert.True(CoverageSamples.IsRunJson(runJson));
        var collection = SpecScenario.Collection();
        var samples = CoverageSamples.FromRunJson(runJson, collection);
        Assert.Equal("POST", samples[0].Method);
        Assert.NotNull(samples[0].Body);

        var report = SpecCoverage.Analyze(SpecScenario.Spec(SpecScenario.V1), samples, "run.json");
        Assert.Equal(2, report[CoverageMetricId.Operations].Covered);
        Assert.Contains(report.Operations, o => o.Key == "GET /pets/{id}" && o.StatusesSeen.SequenceEqual([404]));
        // Without the collection the method is unknown: the path still matches (GET first).
        Assert.Null(CoverageSamples.FromRunJson(runJson)[0].Method);
        Assert.Equal(2, SpecCoverage.Analyze(SpecScenario.Spec(SpecScenario.V1), CoverageSamples.FromRunJson(runJson), "run.json")[CoverageMetricId.Documented].Covered);
    }

    [Fact]
    public void Writers_render_every_metric()
    {
        var report = SpecCoverage.Analyze(SpecScenario.Spec(SpecScenario.V1), CoverageSamples.FromHar(JsonNode.Parse(SpecScenario.Har)!), "traffic.har");
        var text = CoverageReportWriter.Text(report, minimum: 80);
        Assert.Contains("FAIL", text);
        Assert.Contains("DELETE /pets/{id}", text);
        var html = CoverageReportWriter.Html(report, 80);
        Assert.Contains("Operations called", html);
        Assert.Contains("GET /v1/owners", html);
        var json = JsonNode.Parse(CoverageReportWriter.Json(report, 80))!;
        Assert.False(json["passed"]!.GetValue<bool>());
        Assert.Equal(5, json["metrics"]!.AsArray().Count);
    }
}

public class SpecDiffTests
{
    private static SpecDiffReport Diff() => SpecDiff.Compare(SpecScenario.Spec(SpecScenario.V1), SpecScenario.Spec(SpecScenario.V2));

    [Fact]
    public void Classifies_removals_requirements_types_enums_statuses_and_security()
    {
        var report = Diff();
        var breaking = report.Changes.Where(c => c.Severity == ChangeSeverity.Breaking).Select(c => c.ToString()).ToList();

        Assert.Contains("DELETE /pets/{id} · operation: removed", breaking);
        Assert.Contains("GET /pets · query sort: new required parameter", breaking);
        Assert.Contains("GET /pets · query limit: type integer → string", breaking);
        Assert.Contains(breaking, c => c.StartsWith("GET /pets · query status: enum narrowed") && c.Contains("\"sold\""));
        Assert.Contains("GET /pets · response 200: renamed $[*].id → $[*].petId", breaking);
        Assert.Contains("GET /pets/{id} · response 200: renamed $.id → $.petId", breaking);
        Assert.Contains("POST /pets · status: success status 201 → 200", breaking);
        Assert.Contains(breaking, c => c.StartsWith("GET /pets · security: now requires authentication"));

        Assert.Contains(report.Changes, c => c is { Severity: ChangeSeverity.NonBreaking, Operation: "POST /pets", Description: "added $.color (string)" });
        Assert.Contains(report.Changes, c => c is { Severity: ChangeSeverity.Info, Location: "version" });
        Assert.True(report.HasBreaking);
        Assert.StartsWith("9 breaking changes", report.Summary);
    }

    [Fact]
    public void Response_and_request_rules_are_opposites()
    {
        static SpecDocument Spec(string requestRequired, string responseRequired, string responseEnum) => SpecLoader.Parse($$"""
            {"openapi":"3.0.0","info":{"title":"T","version":"1"},"paths":{"/x":{"post":{
              "requestBody":{"content":{"application/json":{"schema":{"type":"object","required":[{{requestRequired}}],"properties":{"a":{"type":"string"},"b":{"type":"string"} } } } } },
              "responses":{"200":{"description":"ok","content":{"application/json":{"schema":{"type":"object","required":[{{responseRequired}}],"properties":{"a":{"type":"string"},"s":{"type":"string","enum":[{{responseEnum}}]} } } } } } } } } } }
            """);
        var report = SpecDiff.Compare(Spec("\"a\"", "\"a\"", "\"x\""), Spec("\"a\",\"b\"", "", "\"x\",\"y\""));

        Assert.Contains(report.Changes, c => c is { Severity: ChangeSeverity.Breaking, Location: "request body", Description: "$.b became required" });
        Assert.Contains(report.Changes, c => c is { Severity: ChangeSeverity.Breaking, Location: "response 200", Description: "$.a is no longer guaranteed (optional now)" });
        Assert.Contains(report.Changes, c => c.Severity == ChangeSeverity.Breaking && c.Location == "response 200" && c.Description.StartsWith("$.s enum widened"));

        var reverse = SpecDiff.Compare(Spec("\"a\",\"b\"", "", "\"x\",\"y\""), Spec("\"a\"", "\"a\"", "\"x\""));
        Assert.Contains(reverse.Changes, c => c is { Severity: ChangeSeverity.NonBreaking, Description: "$.b became optional" });
        Assert.Contains(reverse.Changes, c => c is { Severity: ChangeSeverity.NonBreaking, Description: "$.a is now always present" });
        Assert.Contains(reverse.Changes, c => c.Severity == ChangeSeverity.NonBreaking && c.Description.StartsWith("$.s enum narrowed"));
        Assert.False(reverse.HasBreaking);
    }

    [Fact]
    public void Moved_paths_are_paired_by_operation_id()
    {
        var before = SpecLoader.Parse("""{"openapi":"3.0.0","info":{"title":"T","version":"1"},"paths":{"/users":{"get":{"operationId":"list","responses":{"200":{"description":"ok"}}}}}}""");
        var after = SpecLoader.Parse("""{"openapi":"3.0.0","info":{"title":"T","version":"1"},"paths":{"/accounts":{"get":{"operationId":"list","responses":{"200":{"description":"ok"}}}}}}""");
        var change = Assert.Single(SpecDiff.Compare(before, after).Changes);
        Assert.Equal(("GET /users", ChangeSeverity.Breaking, "moved to /accounts"), (change.Operation, change.Severity, change.Description));
        Assert.Empty(SpecDiff.Compare(before, before).Changes);
    }

    [Fact]
    public void Impact_lists_what_the_collection_breaks_on()
    {
        var collection = SpecScenario.Collection();
        var getPet = collection.Requests.Single(r => r.Name == "getPet");
        getPet.Assertions.Add(new Assertion { Source = ValueSource.JsonPath, Path = "$.id", Operator = AssertionOperator.Exists });
        getPet.Extractions.Add(new ExtractionRule { Variable = "petId", Path = "$.id" });
        var deletePet = collection.Requests.Single(r => r.Name == "deletePet");
        deletePet.Url = "{{baseUrl}}/pets/{{petId}}";
        var flow = new TestFlow { Name = "Pet lifecycle", CollectionId = collection.Id, Steps = [new FlowStep { Type = FlowStepType.Request, RequestId = getPet.Id }] };

        var report = Diff();
        SpecDiff.AddImpact(report, SpecScenario.Spec(SpecScenario.V1), collection, [flow]);

        Assert.Equal(collection.Name, report.CollectionName);
        var breaks = report.Impact.Where(i => i.Severity == ImpactSeverity.Breaks).ToList();
        Assert.Contains(breaks, i => i.Owner == "deletePet" && i.Detail.Contains("removed"));
        Assert.Contains(breaks, i => i.Owner == "listPets" && i.Detail.Contains("query sort"));
        Assert.Contains(breaks, i => i.Owner == "createPet" && i.Kind == "Assertion" && i.Suggestion == "Change the expected status to 200.");
        Assert.Contains(breaks, i => i.Owner == "getPet" && i.Kind == "Assertion" && i.Suggestion == "Change the path to $.petId.");
        Assert.Contains(breaks, i => i.Owner == "getPet" && i.Kind == "Extraction");
        Assert.Contains(breaks, i => i.Owner == "deletePet" && i.Kind == "VariableUse" && i.Detail.Contains("{{petId}}"));
        Assert.Contains(report.Impact, i => i.Owner == "Pet lifecycle" && i.Kind == "Flow");
        Assert.Contains(report.Impact, i => i.Owner == "listPets" && i.Severity == ImpactSeverity.Possible && i.Detail.Contains("enum narrowed"));
        Assert.Contains("break", report.Summary);

        var html = SpecDiffReportWriter.Html(report);
        Assert.Contains("Impact on Pets", html);
        var json = JsonNode.Parse(SpecDiffReportWriter.Json(report))!;
        Assert.True(json["breaking"]!.GetValue<bool>());
        Assert.NotEmpty(json["impact"]!.AsArray());
        Assert.Contains("Breaks in Pets", SpecDiffReportWriter.Text(report));
    }
}

[Collection("Console")]
public class SpecCliTests
{
    [Fact]
    public async Task Coverage_gates_on_min_and_writes_reports()
    {
        var dir = Cli.TempDir();
        var spec = Path.Combine(dir, "pets.yaml");
        var har = Path.Combine(dir, "traffic.har");
        await File.WriteAllTextAsync(spec, SpecScenario.V1);
        await File.WriteAllTextAsync(har, SpecScenario.Har);
        var db = Path.Combine(dir, "d.sqlite");

        var (exit, output) = await Cli.RunAsync("coverage", spec, har, "--min", "80", "-r", "cli,json,html", "-o", Path.Combine(dir, "out"), "--db", db, "--no-color");
        Assert.Equal(Dispatch.Cli.Program.ExitTestsFailed, exit);
        Assert.Contains("FAIL", output);
        Assert.Contains("DELETE /pets/{id}", output);
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "out"), "*.json"));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "out"), "*.html"));

        (exit, output) = await Cli.RunAsync("coverage", spec, har, "--min", "50", "--db", db, "--no-color");
        Assert.Equal(Dispatch.Cli.Program.ExitOk, exit);
        Assert.Contains("pass", output);

        // The spec itself imports as a collection: every operation is called once.
        (exit, output) = await Cli.RunAsync("coverage", spec, spec, "--db", db, "--no-color");
        Assert.Equal(Dispatch.Cli.Program.ExitOk, exit);
        Assert.Contains("Operations called        4/4", output);

        (exit, _) = await Cli.RunAsync("coverage", spec, har, "--min", "150", "--db", db);
        Assert.Equal(Dispatch.Cli.Program.ExitError, exit);
    }

    [Fact]
    public async Task Coverage_can_run_the_collection_live()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/pets", (HttpContext ctx) => ctx.Request.Query.ContainsKey("status") ? Results.Json(Array.Empty<object>()) : Results.BadRequest());
            app.MapPost("/pets", () => Results.Json(new { id = 1, name = "Rex", kind = "dog" }, statusCode: 201));
            app.MapGet("/pets/{id}", (int id) => id == 0 ? Results.NotFound() : Results.Json(new { id, name = "Rex", kind = "dog" }));
            app.MapDelete("/pets/{id}", () => Results.NoContent());
        });
        var dir = Cli.TempDir();
        var spec = Path.Combine(dir, "pets.yaml");
        await File.WriteAllTextAsync(spec, SpecScenario.V1);
        var collection = SpecScenario.Collection();
        collection.Variables.First(v => v.Key == "baseUrl").Value = server.BaseUrl;
        var file = Path.Combine(dir, "pets.dispatch.json");
        await File.WriteAllTextAsync(file, DispatchFormat.ExportCollection(collection));

        var (exit, output) = await Cli.RunAsync("coverage", spec, file, "--run", "--db", Path.Combine(dir, "d.sqlite"), "--no-color");
        Assert.Equal(Dispatch.Cli.Program.ExitOk, exit);
        Assert.Contains("Running Pets", output);
        Assert.Contains("Operations called        4/4", output);
        Assert.Matches(@"GET /pets/\{id\}\s+200 never seen", output); // the imported id example is 0 → 404 seen, 200 is the gap
    }

    [Fact]
    public async Task Breaking_fails_on_breaking_changes_and_lists_the_collection_impact()
    {
        var dir = Cli.TempDir();
        var v1 = Path.Combine(dir, "v1.yaml");
        var v2 = Path.Combine(dir, "v2.yaml");
        await File.WriteAllTextAsync(v1, SpecScenario.V1);
        await File.WriteAllTextAsync(v2, SpecScenario.V2);
        var db = Path.Combine(dir, "d.sqlite");

        var (exit, output) = await Cli.RunAsync("breaking", v1, v2, "--collection", v1, "-r", "cli,json", "-o", Path.Combine(dir, "out"), "--db", db, "--no-color");
        Assert.Equal(Dispatch.Cli.Program.ExitTestsFailed, exit);
        Assert.Contains("Breaking (9)", output);
        Assert.Contains("deletePet · URL", output);
        Assert.Contains("Change the expected status to 200.", output);
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "out"), "*.json"));

        (exit, _) = await Cli.RunAsync("breaking", v1, v2, "--fail-on", "never", "--db", db);
        Assert.Equal(Dispatch.Cli.Program.ExitOk, exit);
        (exit, output) = await Cli.RunAsync("breaking", v1, v1, "--fail-on", "any", "--db", db, "--no-color");
        Assert.Equal(Dispatch.Cli.Program.ExitOk, exit);
        Assert.Contains("No differences", output);
        (exit, _) = await Cli.RunAsync("breaking", v1, v2, "--fail-on", "sometimes", "--db", db);
        Assert.Equal(Dispatch.Cli.Program.ExitError, exit);
    }
}
