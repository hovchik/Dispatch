using Dispatch.Application.Help;
using Dispatch.Application.Interop;
using Dispatch.Application.Lint;
using Dispatch.Application.Sync;
using Dispatch.Domain;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Tests;

public class CollectionLinterTests
{
    private static RequestCollection Collection(params ApiRequest[] requests)
    {
        var collection = new RequestCollection { Name = "Shop", Description = "The shop API", Variables = [new("baseUrl", "https://api.example.com")] };
        foreach (var r in requests)
        {
            r.CollectionId = collection.Id;
            r.Description = "described";
            collection.Requests.Add(r);
        }
        return collection;
    }

    private static ApiRequest Get(string name, string url, params Assertion[] assertions) => new()
    {
        Name = name, Url = url, Assertions = assertions.ToList()
    };

    private static readonly Assertion Ok = new() { Source = ValueSource.Status, Operator = AssertionOperator.Equals, Expected = "200" };

    private static IEnumerable<LintProblem> Of(LintReport report, string rule) => report.Problems.Where(p => p.RuleId == rule);

    [Fact]
    public void Clean_collection_has_no_problems()
    {
        var report = CollectionLinter.Lint(Collection(Get("List", "{{baseUrl}}/items", Ok)));
        Assert.Empty(report.Problems);
        Assert.Contains("No problems", report.Summary);
        Assert.DoesNotContain(report.RulesChecked, r => r.Id == LintRules.MissingDescription); // off by default
    }

    [Fact]
    public void Finds_undefined_and_unused_variables_and_unused_extractions()
    {
        var login = Get("Login", "{{baseUrl}}/login", Ok);
        login.Extractions.Add(new ExtractionRule { Variable = "token", Path = "$.token" });
        login.Extractions.Add(new ExtractionRule { Variable = "orphan", Path = "$.x" });
        var me = Get("Me", "{{baseUrl}}/me?page={{page}}", Ok);
        me.Headers.Add(new KeyValueItem("Authorization", "Bearer {{token}}"));
        me.TestScript = "pm.environment.set('fromScript', 1); pm.test('ok', () => {});";
        var profile = Get("Profile", "{{host}}/profile/{{fromScript}}/{{$guid}}", Ok);
        var collection = Collection(login, me, profile);
        collection.Variables.Add(new KeyValueItem("unused", "x"));

        var report = CollectionLinter.Lint(collection);

        var undefined = Of(report, LintRules.UndefinedVariable).ToList();
        Assert.Equal(["page", "host"], undefined.Select(p => p.Message.Split("}}")[0].Replace("{{", "")).ToArray());
        Assert.All(undefined, p => Assert.Equal(LintSeverity.Warning, p.Severity));
        Assert.Single(Of(report, LintRules.UnusedVariable), p => p.Message.Contains("'unused'"));
        Assert.Single(Of(report, LintRules.UnusedExtraction), p => p.RequestName == "Login" && p.Location == "extraction orphan");

        // An environment that defines the variable silences the warning.
        var withEnv = CollectionLinter.Lint(collection, new LintOptions { Environments = [new ApiEnvironment { Variables = [new("host", "h"), new("page", "1")] }] });
        Assert.Empty(Of(withEnv, LintRules.UndefinedVariable));
    }

    [Fact]
    public void Finds_hardcoded_secrets_by_shape_and_by_name_and_masks_them()
    {
        var jwt = Get("JWT", "https://x/api", Ok);
        jwt.Headers.Add(new KeyValueItem("Authorization", "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U"));
        var apiKey = Get("Key", "https://x/api", Ok);
        apiKey.QueryParams.Add(new KeyValueItem("api_key", "sk-live-1234567890abcdef"));
        var basic = Get("Basic", "https://x/api", Ok);
        basic.Auth.Mode = AuthMode.Basic;
        basic.Auth.Username = "admin";
        basic.Auth.Password = "hunter2hunter2";
        var fine = Get("Fine", "https://x/api", Ok);
        fine.Headers.Add(new KeyValueItem("Authorization", "Bearer {{token}}"));
        fine.Auth.Mode = AuthMode.Bearer;
        fine.Auth.Token = "{{token}}";
        var collection = Collection(jwt, apiKey, basic, fine);
        collection.Variables.Add(new KeyValueItem("token", "AKIAIOSFODNN7EXAMPLE"));
        collection.Variables.Add(new KeyValueItem("clientSecret", "s3cr3t-value-here"));
        collection.Variables.Add(new KeyValueItem("secretButMarked", "s3cr3t-value-here") { IsSecret = true });

        var report = CollectionLinter.Lint(collection);
        var secrets = Of(report, LintRules.HardcodedSecret).ToList();

        Assert.All(secrets, p => Assert.Equal(LintSeverity.Error, p.Severity));
        Assert.Contains(secrets, p => p.RequestName == "JWT" && p.Message.Contains("JSON Web Token"));
        Assert.Contains(secrets, p => p.RequestName == "Key" && p.Location == "query api_key");
        Assert.Contains(secrets, p => p.RequestName == "Basic" && p.Location == "auth Password");
        Assert.Contains(secrets, p => p.RequestId is null && p.Location == "variable token" && p.Message.Contains("AWS access key"));
        Assert.Contains(secrets, p => p.RequestId is null && p.Location == "variable clientSecret");
        Assert.DoesNotContain(secrets, p => p.Location == "variable secretButMarked");
        Assert.DoesNotContain(secrets, p => p.RequestName == "Fine");
        // Never the whole value.
        Assert.All(secrets, p => Assert.DoesNotContain("hunter2hunter2", p.Message));
        Assert.Equal("AKIA…PLE", CollectionLinter.Mask("AKIAIOSFODNN7EXAMPLE"));
    }

    [Fact]
    public void Finds_requests_without_checks_duplicates_and_stale_examples()
    {
        var a = Get("Items", "https://x/items");
        var b = Get("Items again", "https://x/items/");
        b.TestScript = "pm.test('status', function () { pm.response.to.have.status(200); });";
        var c = Get("Items", "https://x/items?sort=asc", Ok);
        c.Expectations.Add(new MessageExpectation());
        c.Examples.Add(new ResponseExample { Name = "Sorted", MatchQuery = [new("sort", "asc"), new("page", "2")], Body = "{\"a\":1}" });
        c.Examples.Add(new ResponseExample { Name = "Broken", Body = "not json" });
        c.Examples.Add(new ResponseExample { Name = "Wrong shape", Schema = "{\"type\":\"array\"}", Body = "{\"a\":1}" });
        c.Examples.Add(new ResponseExample { Name = "Ok", Schema = "{\"type\":\"object\"}", Body = "{\"a\":1}", ContentType = "text/plain" });
        var d = new ApiRequest { Name = "No url", Kind = RequestKind.Http };

        var report = CollectionLinter.Lint(Collection(a, b, c, d));

        Assert.Equal(["Items", "No url"], Of(report, LintRules.NoAssertions).Select(p => p.RequestName).ToArray());
        Assert.Single(Of(report, LintRules.DuplicateRequest), p => p.RequestName == "Items again" && p.Message.Contains("'Items'"));
        Assert.Single(Of(report, LintRules.DuplicateName), p => p.RequestName == "Items");
        var stale = Of(report, LintRules.StaleExample).ToList();
        Assert.Contains(stale, p => p.Location == "example Sorted" && p.Message.Contains("'page'"));
        Assert.DoesNotContain(stale, p => p.Location == "example Sorted" && p.Message.Contains("'sort'"));
        Assert.Contains(stale, p => p.Location == "example Broken" && p.Message.Contains("not valid JSON"));
        Assert.Contains(stale, p => p.Location == "example Wrong shape" && p.Message.Contains("an array"));
        Assert.DoesNotContain(stale, p => p.Location == "example Ok");
        Assert.Single(Of(report, LintRules.EmptyUrl), p => p.RequestName == "No url");
        Assert.Equal(LintSeverity.Error, report.Highest);
        Assert.True(report.FailsAt(LintSeverity.Warning));
    }

    [Fact]
    public void Config_switches_rules_off_changes_severities_and_round_trips()
    {
        var collection = Collection(Get("Items", "https://x/items"));
        collection.Description = "";
        collection.Requests[0].Description = "";

        var defaults = CollectionLinter.Lint(collection);
        Assert.Empty(Of(defaults, LintRules.MissingDescription));
        Assert.Single(Of(defaults, LintRules.NoAssertions));

        var config = LintConfig.Parse("""{"rules":{"DP004":"off","DP008":"error","DP002":true}}""");
        var tuned = CollectionLinter.Lint(collection, new LintOptions { Config = config });
        Assert.Empty(Of(tuned, LintRules.NoAssertions));
        Assert.Equal(2, Of(tuned, LintRules.MissingDescription).Count());
        Assert.All(Of(tuned, LintRules.MissingDescription), p => Assert.Equal(LintSeverity.Error, p.Severity));

        var again = LintConfig.Parse(config.ToJson());
        Assert.Equal(config.Rules.OrderBy(r => r.Key), again.Rules.OrderBy(r => r.Key));
        Assert.Throws<FormatException>(() => LintConfig.ParseSeverity("loud"));
    }

    [Fact]
    public void Reports_render_in_every_format()
    {
        var request = Get("Items", "https://x/items?token=abcdefgh12345678");
        var report = CollectionLinter.Lint(Collection(request));

        var text = LintReportWriter.Text(report);
        Assert.Contains("DP003", text);
        Assert.Contains("DP004", text);
        Assert.DoesNotContain("abcdefgh12345678", text);

        var html = LintReportWriter.Html(report);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("Hard-coded secret", html);

        var json = System.Text.Json.Nodes.JsonNode.Parse(LintReportWriter.Json(report))!;
        Assert.Equal(1, (int)json["errors"]!);
        Assert.Equal("DP003", json["problems"]![0]!["rule"]!.ToString());
        Assert.Equal(request.Id.ToString(), json["problems"]![0]!["requestId"]!.ToString());
    }

    [Fact]
    public void Sample_collection_passes_the_default_rules_at_error_level()
    {
        var report = CollectionLinter.Lint(SampleCollection.Create());
        Assert.False(report.FailsAt(LintSeverity.Error), string.Join("\n", report.Problems.Where(p => p.Severity == LintSeverity.Error).Select(p => p.Message)));
    }
}

public class SyncEngineTests
{
    private static Dictionary<string, string> Map(params (string Key, string Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);

    [Fact]
    public void Attributes_each_change_to_a_side_and_flags_both_sided_edits()
    {
        var last = Map(("same", "v1"), ("appEdit", "v1"), ("folderEdit", "v1"), ("both", "v1"), ("appDel", "v1"), ("folderDel", "v1"), ("appDelFolderEdit", "v1"));
        var app = Map(("same", "v1"), ("appEdit", "v2"), ("folderEdit", "v1"), ("both", "a"), ("folderDel", "v1"), ("newInApp", "n"), ("agree", "x"));
        var folder = Map(("same", "v1"), ("appEdit", "v1"), ("folderEdit", "v2"), ("both", "f"), ("appDel", "v1"), ("newInFolder", "n"), ("appDelFolderEdit", "v2"), ("agree", "x"));

        var plan = SyncEngine.Plan(app, folder, last).ToDictionary(d => d.Key, d => d.Action);

        Assert.Equal(SyncAction.None, plan["same"]);
        Assert.Equal(SyncAction.None, plan["agree"]);
        Assert.Equal(SyncAction.ToFolder, plan["appEdit"]);
        Assert.Equal(SyncAction.ToApp, plan["folderEdit"]);
        Assert.Equal(SyncAction.Conflict, plan["both"]);
        Assert.Equal(SyncAction.DeleteInFolder, plan["appDel"]);
        Assert.Equal(SyncAction.DeleteInApp, plan["folderDel"]);
        Assert.Equal(SyncAction.ToFolder, plan["newInApp"]);
        Assert.Equal(SyncAction.ToApp, plan["newInFolder"]);
        Assert.Equal(SyncAction.Conflict, plan["appDelFolderEdit"]);

        var next = SyncEngine.NextState(SyncEngine.Plan(app, folder, last));
        Assert.Equal("v2", next["appEdit"]);
        Assert.Equal("v2", next["folderEdit"]);
        Assert.Equal("v1", next["both"]); // unresolved: keeps the old base
        Assert.False(next.ContainsKey("appDel"));
        Assert.False(next.ContainsKey("folderDel"));
        Assert.Equal("n", next["newInFolder"]);
    }

    [Fact]
    public void First_sync_copies_one_sided_items_and_conflicts_on_differences()
    {
        var plan = SyncEngine.Plan(Map(("a", "1"), ("c", "x")), Map(("b", "2"), ("c", "y")), Map()).ToDictionary(d => d.Key, d => d.Action);
        Assert.Equal(SyncAction.ToFolder, plan["a"]);
        Assert.Equal(SyncAction.ToApp, plan["b"]);
        Assert.Equal(SyncAction.Conflict, plan["c"]);
    }

    [Fact]
    public void Resolving_makes_the_next_plan_copy_the_winner()
    {
        var last = Map(("k", "v1"), ("gone", "v1"));
        var app = Map(("k", "a"), ("gone", "edited"));
        var folder = Map(("k", "f"));
        var plan = SyncEngine.Plan(app, folder, last);

        SyncEngine.Resolve(last, plan.Single(d => d.Key == "k"), SyncSide.App);
        SyncEngine.Resolve(last, plan.Single(d => d.Key == "gone"), SyncSide.Folder);
        var next = SyncEngine.Plan(app, folder, last).ToDictionary(d => d.Key, d => d.Action);
        Assert.Equal(SyncAction.ToFolder, next["k"]);
        Assert.Equal(SyncAction.DeleteInApp, next["gone"]);

        SyncEngine.Resolve(last, plan.Single(d => d.Key == "gone"), SyncSide.App);
        Assert.Equal(SyncAction.ToFolder, SyncEngine.Plan(app, folder, last).Single(d => d.Key == "gone").Action);
    }
}

public class FolderSyncServiceTests : IAsyncLifetime
{
    private readonly string _root = Cli.TempDir();
    private CollectionRepository _repository = null!;
    private FolderSyncService _sync = null!;
    private RequestCollection _collection = null!;

    public async Task InitializeAsync()
    {
        var factory = new DbFactory(Path.Combine(_root, "test.db"));
        await new DatabaseInitializer(factory).InitializeAsync();
        _repository = new CollectionRepository(factory);
        _sync = new FolderSyncService(_repository, Path.Combine(_root, "state"));
        _collection = new RequestCollection { Name = "Shop", Variables = [new("baseUrl", "https://x"), new("pw", "secret!") { IsSecret = true }] };
        await _repository.AddAsync(_collection);
        foreach (var (name, folder) in new[] { ("List", ""), ("Create", "Admin"), ("Delete", "Admin") })
            await _repository.SaveRequestAsync(new ApiRequest { Name = name, Folder = folder, Url = "{{baseUrl}}/" + name.ToLowerInvariant(), CollectionId = _collection.Id });
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    private string Folder => Path.Combine(_root, "repo");

    private async Task<RequestCollection> AppAsync() => (await _repository.GetAllAsync()).Single(c => c.Id == _collection.Id);

    private string RequestFile(string name) => Directory.EnumerateFiles(Folder, $"*-{name.ToLowerInvariant()}.request.json", SearchOption.AllDirectories).Single();

    [Fact]
    public async Task First_sync_exports_then_edits_flow_both_ways()
    {
        var first = await _sync.SyncAsync(_collection.Id, Folder);
        Assert.Equal(3, first.ToFolder); // an empty folder takes the collection's metadata as-is
        Assert.Empty(first.Conflicts);
        Assert.True(File.Exists(Path.Combine(Folder, "admin", "001-create.request.json")));
        Assert.DoesNotContain("secret!", File.ReadAllText(Path.Combine(Folder, DispatchFormat.CollectionFileName)));

        Assert.True((await _sync.SyncAsync(_collection.Id, Folder)).InSync);
        Assert.Equal(Folder, _sync.LastFolder(_collection.Id));

        // Edit in the folder (as git would), add a file, delete a file.
        var listFile = RequestFile("List");
        File.WriteAllText(listFile, File.ReadAllText(listFile).Replace("{{baseUrl}}/list", "{{baseUrl}}/items?page=1"));
        File.WriteAllText(Path.Combine(Folder, "admin", "zzz-new.request.json"), """{"name":"Audit","url":"{{baseUrl}}/audit","method":"Get"}""");
        File.Delete(RequestFile("Delete"));
        // Edit in the app.
        var app = await AppAsync();
        var create = app.Requests.Single(r => r.Name == "Create");
        create.Method = HttpVerb.Post;
        await _repository.SaveRequestAsync(create);

        var second = await _sync.SyncAsync(_collection.Id, Folder);

        Assert.Empty(second.Conflicts);
        Assert.Equal(3, second.ToApp);
        Assert.Equal(1, second.ToFolder);
        app = await AppAsync();
        Assert.Equal("{{baseUrl}}/items?page=1", app.Requests.Single(r => r.Name == "List").Url);
        Assert.Equal("Admin", app.Requests.Single(r => r.Name == "Audit").Folder);
        Assert.DoesNotContain(app.Requests, r => r.Name == "Delete");
        Assert.Equal("secret!", app.Variables.Single(v => v.Key == "pw").Value);
        Assert.Contains("\"method\": \"Post\"", File.ReadAllText(RequestFile("Create")));
        // The hand-written file was rewritten in canonical form with a stable id.
        var audit = RequestFile("Audit");
        Assert.Contains(app.Requests.Single(r => r.Name == "Audit").Id.ToString(), File.ReadAllText(audit));
        Assert.True((await _sync.SyncAsync(_collection.Id, Folder)).InSync);
    }

    [Fact]
    public async Task Conflicts_leave_both_sides_alone_until_resolved()
    {
        await _sync.SyncAsync(_collection.Id, Folder);
        var listFile = RequestFile("List");
        File.WriteAllText(listFile, File.ReadAllText(listFile).Replace("{{baseUrl}}/list", "{{baseUrl}}/from-folder"));
        var app = await AppAsync();
        var list = app.Requests.Single(r => r.Name == "List");
        list.Url = "{{baseUrl}}/from-app";
        await _repository.SaveRequestAsync(list);
        // Also: deleted in the folder, renamed in the app.
        File.Delete(RequestFile("Delete"));
        var delete = app.Requests.Single(r => r.Name == "Delete");
        delete.Name = "Remove";
        await _repository.SaveRequestAsync(delete);

        var result = await _sync.SyncAsync(_collection.Id, Folder);

        Assert.Equal(2, result.Conflicts.Count);
        Assert.Contains(result.Conflicts, c => c.Key == list.Id.ToString() && c.AppJson is not null && c.FolderJson is not null);
        Assert.Contains(result.Conflicts, c => c.Key == delete.Id.ToString() && c.FolderJson is null && c.Description.Contains("deleted in the folder"));
        Assert.Equal("{{baseUrl}}/from-app", (await AppAsync()).Requests.Single(r => r.Name == "List").Url);
        Assert.Contains("from-folder", File.ReadAllText(RequestFile("List")));
        Assert.Empty(Directory.EnumerateFiles(Folder, "*-remove.request.json", SearchOption.AllDirectories));
        // Still a conflict on the next run.
        Assert.Equal(2, (await _sync.SyncAsync(_collection.Id, Folder)).Conflicts.Count);

        var resolved = await _sync.ResolveAsync(_collection.Id, Folder, list.Id.ToString(), SyncSide.Folder);
        Assert.Single(resolved.Conflicts);
        Assert.Equal("{{baseUrl}}/from-folder", (await AppAsync()).Requests.Single(r => r.Name == "List").Url);

        var all = await _sync.SyncAsync(_collection.Id, Folder, SyncSide.App);
        Assert.Empty(all.Conflicts);
        Assert.Single(Directory.EnumerateFiles(Folder, "*-remove.request.json", SearchOption.AllDirectories));
        Assert.True((await _sync.SyncAsync(_collection.Id, Folder)).InSync);
    }

    [Fact]
    public async Task Collection_metadata_syncs_too()
    {
        await _sync.SyncAsync(_collection.Id, Folder);
        var meta = Path.Combine(Folder, DispatchFormat.CollectionFileName);
        File.WriteAllText(meta, File.ReadAllText(meta).Replace("\"name\": \"Shop\"", "\"name\": \"Shop v2\""));

        var result = await _sync.SyncAsync(_collection.Id, Folder);

        Assert.Contains(result.Changes, c => c.Key == FolderSyncService.CollectionKey && c.Action == SyncAction.ToApp);
        var app = await AppAsync();
        Assert.Equal("Shop v2", app.Name);
        Assert.Equal("secret!", app.Variables.Single(v => v.Key == "pw").Value);
    }

    [Fact]
    public async Task Another_folder_starts_from_scratch_instead_of_reusing_the_baseline()
    {
        await _sync.SyncAsync(_collection.Id, Folder);
        var other = Path.Combine(_root, "other");

        var result = await _sync.SyncAsync(_collection.Id, other);

        Assert.Equal(3, result.ToFolder);
        Assert.Equal(0, result.ToApp);
        Assert.Equal(3, (await AppAsync()).Requests.Count);
        Assert.Equal(3, Directory.EnumerateFiles(other, "*.request.json", SearchOption.AllDirectories).Count());
        // Going back to the first folder also starts over (and finds everything in agreement).
        Assert.True((await _sync.SyncAsync(_collection.Id, Folder)).InSync);
    }

    [Fact]
    public async Task Unreadable_request_file_stops_the_sync_before_anything_changes()
    {
        await _sync.SyncAsync(_collection.Id, Folder);
        var listFile = RequestFile("List");
        File.WriteAllText(listFile, "{ not json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.SyncAsync(_collection.Id, Folder));

        Assert.Contains("could not be read", ex.Message);
        Assert.Equal(3, (await AppAsync()).Requests.Count);
        Assert.Equal("{ not json", File.ReadAllText(listFile));
    }

    [Fact]
    public async Task Watcher_picks_up_folder_edits()
    {
        await _sync.SyncAsync(_collection.Id, Folder);
        var synced = new TaskCompletionSource<SyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = _sync.Watch(_collection.Id, Folder, TimeSpan.FromHours(1),
            r => { if (!r.InSync) synced.TrySetResult(r); }, ex => synced.TrySetException(ex));

        var listFile = RequestFile("List");
        File.WriteAllText(listFile, File.ReadAllText(listFile).Replace("{{baseUrl}}/list", "{{baseUrl}}/watched"));

        var result = await synced.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, result.ToApp);
        Assert.Equal("{{baseUrl}}/watched", (await AppAsync()).Requests.Single(r => r.Name == "List").Url);
    }

    private sealed class DbFactory(string path) : IDbContextFactory<DispatchDbContext>
    {
        public DispatchDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<DispatchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}

[Collection("Console")]
public class LintCliTests
{
    [Fact]
    public async Task Lint_command_reports_and_gates()
    {
        var dir = Cli.TempDir();
        var collection = new RequestCollection { Name = "Cli lint", Description = "d" };
        collection.Requests.Add(new ApiRequest { Name = "Items", Url = "https://x/items?api_key=abcdefgh12345678", CollectionId = collection.Id, Description = "d" });
        var file = Path.Combine(dir, "c.dispatch.json");
        await File.WriteAllTextAsync(file, DispatchFormat.ExportCollection(collection));
        var db = Path.Combine(dir, "lint.db");

        var (exit, output) = await Cli.RunAsync("lint", file, "--db", db, "-r", "cli,json", "-o", dir, "--no-color");
        Assert.Equal(1, exit);
        Assert.Contains("DP003", output);
        Assert.Contains("DP004", output);
        Assert.Single(Directory.GetFiles(dir, "*-lint-*.json"));

        (exit, output) = await Cli.RunAsync("lint", file, "--db", db, "--fail-on", "never", "--disable", "DP003", "--no-color");
        Assert.Equal(0, exit);
        Assert.DoesNotContain("DP003", output);

        await File.WriteAllTextAsync(Path.Combine(dir, LintConfig.FileName), """{"rules":{"DP003":"off","DP004":"off"}}""");
        (exit, output) = await Cli.RunAsync("lint", file, "--db", db, "--fail-on", "info", "--no-color");
        Assert.Equal(0, exit);
        Assert.Contains("No problems", output);

        (exit, output) = await Cli.RunAsync("lint", "--list-rules");
        Assert.Equal(0, exit);
        Assert.Contains("DP001", output);

        (exit, output) = await Cli.RunAsync("lint", file, "--db", db, "--disable", "DP999");
        Assert.Equal(2, exit);
        Assert.Contains("Unknown lint rule", output);
    }

    [Fact]
    public async Task Sync_command_needs_a_saved_collection_and_round_trips()
    {
        var dir = Cli.TempDir();
        var db = Path.Combine(dir, "sync.db");
        var (exit, output) = await Cli.RunAsync("sync", "Nope", Path.Combine(dir, "repo"), "--db", db);
        Assert.Equal(2, exit);
        Assert.Contains("not a collection saved in the app", output);

        var collection = new RequestCollection { Name = "Synced" };
        collection.Requests.Add(new ApiRequest { Name = "Ping", Url = "https://x/ping", CollectionId = collection.Id });
        var file = Path.Combine(dir, "s.dispatch.json");
        await File.WriteAllTextAsync(file, DispatchFormat.ExportCollection(collection));
        Assert.Equal(0, (await Cli.RunAsync("import", file, "--db", db)).Exit);

        (exit, output) = await Cli.RunAsync("sync", "Synced", Path.Combine(dir, "repo"), "--db", db);
        Assert.Equal(0, exit);
        Assert.Contains("written to the folder", output);
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "repo"), "*.request.json"));

        (exit, output) = await Cli.RunAsync("sync", "Synced", Path.Combine(dir, "repo"), "--db", db);
        Assert.Equal(0, exit);
        Assert.Contains("In sync", output);
    }
}
