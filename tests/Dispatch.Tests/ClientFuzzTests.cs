using System.Net;
using System.Text.Json.Nodes;
using Dispatch.Application.ClientFuzz;
using Dispatch.Infrastructure.Capture;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dispatch.Tests;

public class MutationTests
{
    private const string Profile = """
        {"user":{"id":7,"name":"Ann","avatar":"a.png","status":"active"},"items":[{"sku":"A"},{"sku":"B"}]}
        """;

    private static readonly IReadOnlySet<MutationKind> All = Enum.GetValues<MutationKind>().ToHashSet();

    [Fact]
    public void Plans_field_array_enum_and_response_level_variations()
    {
        var plan = Mutations.Plan(Profile, All, 200);
        var described = plan.Select(m => (m.Kind, m.Path)).ToList();

        Assert.Equal(MutationKind.ServerError, plan[0].Kind);
        Assert.Contains((MutationKind.NullField, "$.user.avatar"), described);
        Assert.Contains((MutationKind.DropField, "$.user.avatar"), described);
        Assert.Contains((MutationKind.NullField, "$.items[*].sku"), described);
        Assert.Contains((MutationKind.EmptyArray, "$.items"), described);
        Assert.Contains((MutationKind.SingleItem, "$.items"), described);
        Assert.Contains((MutationKind.UnexpectedEnum, "$.user.status"), described);
        Assert.Contains((MutationKind.WrongType, "$.user.id"), described);
        Assert.Contains((MutationKind.HugeString, "$.user.name"), described);
        Assert.Contains((MutationKind.ExtraField, "$"), described);
        Assert.DoesNotContain(described, d => d.Kind == MutationKind.UnexpectedEnum && d.Path == "$.user.name");
    }

    [Fact]
    public void Respects_enabled_kinds_and_the_cap_while_covering_each_kind()
    {
        var kinds = new HashSet<MutationKind> { MutationKind.NullField, MutationKind.EmptyArray, MutationKind.ServerError };
        var plan = Mutations.Plan(Profile, kinds, 4);
        Assert.Equal(4, plan.Count);
        Assert.All(plan, m => Assert.Contains(m.Kind, kinds));
        Assert.Equal(kinds, plan.Select(m => m.Kind).ToHashSet());
    }

    [Fact]
    public void Applies_mutations_to_the_first_array_item_and_skips_missing_fields()
    {
        var response = new InterceptedResponse("GET", "https://a.test/me", 200, "application/json", Profile);
        var nulled = Mutations.Apply(new ResponseMutation(MutationKind.NullField, "$.items[*].sku", ""), response, TimeSpan.Zero)!;
        Assert.Equal("""[{"sku":null},{"sku":"B"}]""", JsonNode.Parse(nulled.Body)!["items"]!.ToJsonString());

        var single = Mutations.Apply(new ResponseMutation(MutationKind.SingleItem, "$.items", ""), response, TimeSpan.Zero)!;
        Assert.Single(JsonNode.Parse(single.Body)!["items"]!.AsArray());

        var wrong = Mutations.Apply(new ResponseMutation(MutationKind.WrongType, "$.user.id", ""), response, TimeSpan.Zero)!;
        Assert.Equal("\"7\"", JsonNode.Parse(wrong.Body)!["user"]!["id"]!.ToJsonString());

        Assert.Null(Mutations.Apply(new ResponseMutation(MutationKind.NullField, "$.user.missing", ""), response, TimeSpan.Zero));
        Assert.Equal(503, Mutations.Apply(new ResponseMutation(MutationKind.Unavailable, "", ""), response, TimeSpan.Zero)!.Status);
        Assert.Equal(TimeSpan.FromSeconds(2), Mutations.Apply(new ResponseMutation(MutationKind.Slow, "", ""), response, TimeSpan.FromSeconds(2))!.Delay);
    }
}

/// <summary>The fuzzer driven by a simulated client with explicit timestamps: deterministic, no real time.</summary>
public class ClientFuzzerTests
{
    private const string Me = """{"id":7,"avatar":"a.png","name":"Ann","items":[{"sku":"A"}]}""";

    /// <summary>
    /// A client with bugs: it builds an avatar URL even when avatar is null, retries a failed /me in a tight loop, stops
    /// working when items is empty, and reports a crash when name is missing. Otherwise it loads /feed and /notifications.
    /// </summary>
    private static List<FuzzExperiment> Simulate(IReadOnlySet<MutationKind> kinds)
    {
        var fuzzer = new ClientFuzzer(new ClientFuzzOptions { Observation = TimeSpan.FromSeconds(5), Kinds = kinds, BaselineResponses = 2 });
        var t = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        void Request(string method, string path, string body = "")
        {
            t = t.AddMilliseconds(20);
            fuzzer.OnRequest(method, "https://app.test" + path, body, t);
        }

        for (var round = 0; round < 40; round++)
        {
            Request("GET", "/api/me");
            var got = fuzzer.Intercept(new InterceptedResponse("GET", "https://app.test/api/me", 200, "application/json", Me), t);
            var status = got?.Status ?? 200;
            var body = JsonNode.Parse(got?.Body is { Length: > 0 } b && b.TrimStart().StartsWith('{') && b.TrimEnd().EndsWith('}') ? b : "{}")!;
            if (status >= 500)
                for (var i = 0; i < 8; i++)
                    Request("GET", "/api/me");
            else if (body["items"] is JsonArray { Count: 0 })
            {
                // stuck: no further requests
            }
            else
            {
                if (body.AsObject().ContainsKey("avatar") && body["avatar"] is null)
                    Request("GET", "/avatars/undefined");
                if (got is not null && body["name"] is null && status == 200 && body.AsObject().Count > 0 && !body.AsObject().ContainsKey("name"))
                    Request("POST", "/api/client-errors", """{"message":"Cannot read properties of undefined (reading 'length')"}""");
                Request("GET", "/api/feed");
                Request("GET", "/api/notifications");
            }
            t = t.AddSeconds(6);
            fuzzer.Tick(t);
        }
        return fuzzer.Experiments.ToList();
    }

    [Fact]
    public void Detects_broken_values_retry_storms_silence_and_error_reports()
    {
        var experiments = Simulate(new HashSet<MutationKind> { MutationKind.NullField, MutationKind.DropField, MutationKind.EmptyArray, MutationKind.ServerError });
        FuzzExperiment Find(MutationKind kind, string path) => experiments.Single(e => e.Mutation.Kind == kind && e.Mutation.Path == path);

        Assert.All(experiments, e => Assert.NotEqual(FuzzVerdict.Running, e.Verdict));
        var avatar = Find(MutationKind.NullField, "$.avatar");
        Assert.Equal(FuzzVerdict.Breaks, avatar.Verdict);
        Assert.Contains(avatar.Signals, s => s.Contains("GET /avatars/undefined contains \"undefined\""));

        var error = Find(MutationKind.ServerError, "");
        Assert.Contains(error.Signals, s => s.StartsWith("retry storm: 8 requests to GET /api/me"));

        var empty = Find(MutationKind.EmptyArray, "$.items");
        Assert.Contains(empty.Signals, s => s.StartsWith("went silent"));

        var missingName = Find(MutationKind.DropField, "$.name");
        Assert.Contains(missingName.Signals, s => s == "reported an error: POST /api/client-errors");

        Assert.Equal(FuzzVerdict.Copes, Find(MutationKind.NullField, "$.id").Verdict);
        Assert.Contains("broke the client", ClientFuzzReport.Summary(experiments));
        Assert.Contains("$.avatar is null", ClientFuzzReport.Html(experiments));
    }

    [Fact]
    public void Only_one_experiment_runs_at_a_time_and_baseline_responses_pass_through()
    {
        var fuzzer = new ClientFuzzer(new ClientFuzzOptions { BaselineResponses = 2, Kinds = new HashSet<MutationKind> { MutationKind.NullField } });
        var t = DateTimeOffset.UnixEpoch;
        var response = new InterceptedResponse("GET", "https://app.test/api/me", 200, "application/json", Me);
        Assert.Null(fuzzer.Intercept(response, t));
        Assert.Null(fuzzer.Intercept(response, t.AddSeconds(1)));
        Assert.NotNull(fuzzer.Intercept(response, t.AddSeconds(2)));
        Assert.Null(fuzzer.Intercept(response, t.AddSeconds(3))); // still watching the first experiment
        Assert.Single(fuzzer.Experiments);
        Assert.NotNull(fuzzer.Intercept(response, t.AddSeconds(30))); // window over → next one
        Assert.Equal(2, fuzzer.Experiments.Count);
    }
}

/// <summary>The fuzzer inside the real capture proxy, with a client going through it.</summary>
public class ClientFuzzProxyTests
{
    [Fact]
    public async Task The_proxy_feeds_mutated_responses_to_a_real_client_and_spots_the_break()
    {
        var avatarRequests = new List<string>();
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/api/me", () => Results.Json(new { id = 7, avatar = "a.png" }));
            app.MapGet("/avatars/{name}", (string name) =>
            {
                lock (avatarRequests)
                    avatarRequests.Add(name);
                return Results.Ok();
            });
        });
        var fuzzer = new ClientFuzzer(new ClientFuzzOptions
        {
            Observation = TimeSpan.FromMilliseconds(400), BaselineResponses = 1, Kinds = new HashSet<MutationKind> { MutationKind.NullField }
        });
        using var ca = new CertificateAuthority();
        await using var proxy = new CaptureProxy(ca);
        await proxy.StartAsync(new CaptureProxyOptions { Port = 0, Interceptor = fuzzer });
        using var client = new HttpClient(new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{proxy.Port}"), UseProxy = true });

        // A buggy client: renders the avatar from whatever /me returned.
        for (var i = 0; i < 6 && fuzzer.Experiments.Count(e => e.Verdict != FuzzVerdict.Running) < 2; i++)
        {
            var me = await client.GetAsync($"{server.BaseUrl}/api/me");
            var json = JsonNode.Parse(await me.Content.ReadAsStringAsync())!;
            var avatar = json["avatar"]?.GetValue<string>() ?? "undefined";
            await client.GetAsync($"{server.BaseUrl}/avatars/{avatar}");
            await Task.Delay(500);
            fuzzer.Tick(DateTimeOffset.Now);
        }

        var avatarExperiment = Assert.Single(fuzzer.Experiments, e => e.Mutation.Path == "$.avatar");
        Assert.Equal(FuzzVerdict.Breaks, avatarExperiment.Verdict);
        Assert.Contains("undefined", avatarRequests);
        Assert.Equal(FuzzVerdict.Copes, Assert.Single(fuzzer.Experiments, e => e.Mutation.Path == "$.id").Verdict);
    }
}

[Collection("Console")]
public class CliFuzzClientTests
{
    [Fact]
    public async Task Fuzz_client_command_reports_breaks_found_through_the_proxy()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/api/me", () => Results.Json(new { avatar = "a.png" }));
            app.MapGet("/avatars/{name}", () => Results.Ok());
        });
        var port = HttpProtocolExecutorTests.FreePort();
        var dir = Cli.TempDir();
        var run = Cli.RunAsync("fuzz-client", "--port", port.ToString(), "--duration", "3s", "--window", "300ms", "--baseline", "1",
            "--kinds", "NullField", "--no-color", "-r", "cli,json", "-o", dir);

        using var client = new HttpClient(new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true });
        var deadline = DateTime.UtcNow.AddSeconds(2.5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var me = JsonNode.Parse(await client.GetStringAsync($"{server.BaseUrl}/api/me"))!;
                await client.GetAsync($"{server.BaseUrl}/avatars/{me["avatar"]?.GetValue<string>() ?? "undefined"}");
            }
            catch (HttpRequestException)
            {
                // The proxy may not be listening yet.
            }
            await Task.Delay(400);
        }

        var (exit, output) = await run;
        Assert.Equal(0, exit);
        Assert.Contains("→ GET /api/me: sending $.avatar is null", output);
        Assert.Contains("✗ BREAKS used a broken value: GET /avatars/undefined", output);
        Assert.Contains("1 of 1 response variation(s) broke the client", output);
        Assert.Contains("\"verdict\": \"Breaks\"", await File.ReadAllTextAsync(Directory.GetFiles(dir, "client-fuzz-*.json").Single()));
    }
}
