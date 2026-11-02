using System.Text.Json.Nodes;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Interop;
using Dispatch.Application.Spec;
using Dispatch.Domain;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Interop;
using Dispatch.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary><c>dispatch openapi</c>: write an OpenAPI 3.0 document from a collection (plus its run history) or a HAR file.</summary>
public static class OpenApiCommand
{
    private const string Usage = "Usage: dispatch openapi <collection | traffic.har> [-o api.yaml|-] [--format yaml|json] [--title t] " +
                                 "[--version v] [--server url] [--no-history] [--no-examples] [--db path]";

    private static readonly HashSet<string> Flags = ["no-history", "no-examples"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["o"] = "out", ["f"] = "format" });
        if (args.Positionals.Count != 1)
            throw new UsageException(Usage);
        var source = args.Positionals[0];
        var outPath = args.Option("out");
        var format = (args.Option("format") ?? (outPath is not null && outPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml"))
            .ToLowerInvariant();
        if (format is not ("yaml" or "yml" or "json"))
            throw new UsageException($"--format expects yaml or json, got '{format}'.");
        var options = new OpenApiOptions
        {
            Title = args.Option("title"),
            Version = args.Option("version") ?? "1.0.0",
            Server = args.Option("server"),
            IncludeExamples = !args.Flag("no-examples")
        };

        // With -o - the document is the only thing on stdout; progress goes to stderr.
        var progress = outPath == "-" ? Console.Error : Console.Out;
        await using var services = Program.BuildServices(args.Option("db"));
        JsonObject spec;
        string slug;
        if (File.Exists(source) && TryHar(source) is { } har)
        {
            spec = OpenApiWriter.FromHar(har, options);
            slug = DispatchFormat.Slug(Path.GetFileNameWithoutExtension(source));
            progress.WriteLine($"Described {OpenApiWriter.OperationCount(spec)} operation(s) from {Path.GetFileName(source)}.");
        }
        else
        {
            var (collection, _) = await new Workspace(services).LoadCollectionAsync(source, args.Option("collection"));
            var history = args.Flag("no-history") ? [] : await HistoryAsync(services, cancellationToken);
            spec = OpenApiWriter.FromCollection(collection, history, options);
            slug = DispatchFormat.Slug(collection.Name);
            progress.WriteLine($"Described {OpenApiWriter.OperationCount(spec)} operation(s) of {collection.Name}" +
                              (history.Count > 0 ? $" (with {history.Count} history entries available)." : "."));
        }

        var content = format == "json" ? OpenApiWriter.ToJson(spec) : OpenApiWriter.ToYaml(spec);
        var path = outPath ?? $"{slug}.openapi.{(format == "json" ? "json" : "yaml")}";
        if (path == "-")
        {
            Console.WriteLine(content);
            return Program.ExitOk;
        }
        await File.WriteAllTextAsync(path, content, cancellationToken);
        Console.WriteLine($"Wrote {Path.GetFullPath(path)}");
        return Program.ExitOk;
    }

    /// <summary>Recent history from the app database (empty for a fresh or unreadable database).</summary>
    private static async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            return await services.GetRequiredService<IHistoryRepository>().GetRecentAsync(5000, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }
    }

    internal static JsonNode? TryHar(string path)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            return root is not null && Har.IsHar(root) ? root : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary><c>dispatch scaffold</c>: create a test collection (happy paths, schema checks, negatives) from an OpenAPI spec.</summary>
public static class ScaffoldCommand
{
    private const string Usage = "Usage: dispatch scaffold <spec.yaml | spec.json | url> [-o tests.dispatch.json|-] [--save] [--no-negatives] " +
                                 "[--no-schema] [--db path]";

    private static readonly HashSet<string> Flags = ["save", "no-negatives", "no-schema"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["o"] = "out" });
        if (args.Positionals.Count != 1)
            throw new UsageException(Usage);
        var source = args.Positionals[0];
        var progress = args.Option("out") == "-" ? Console.Error : Console.Out;
        await using var services = Program.BuildServices(args.Option("db"));

        var (text, location) = await LoadSpecAsync(services, source, cancellationToken);
        var root = ParseSpec(text);
        var report = TestScaffolder.Scaffold(root, location, new ScaffoldOptions
        {
            Negatives = !args.Flag("no-negatives"),
            SchemaAssertions = !args.Flag("no-schema")
        });
        progress.WriteLine(report.Summary);
        foreach (var note in report.Notes)
            Console.Error.WriteLine($"note: {note}");

        if (args.Flag("save"))
        {
            var count = report.Collection.Requests.Count;
            await new Workspace(services).SaveImportAsync(ImportResult.Single("OpenAPI tests", report.Collection));
            Console.WriteLine($"Saved collection '{report.Collection.Name}' ({count} requests) to the app.");
            return Program.ExitOk;
        }

        var content = DispatchFormat.ExportCollection(report.Collection);
        var path = args.Option("out") ?? DispatchFormat.Slug(report.Collection.Name) + ".dispatch.json";
        if (path == "-")
        {
            Console.WriteLine(content);
            return Program.ExitOk;
        }
        await File.WriteAllTextAsync(path, content, cancellationToken);
        Console.WriteLine($"Wrote {report.Collection.Requests.Count} request(s) to {Path.GetFullPath(path)}");
        return Program.ExitOk;
    }

    private static async Task<(string Text, string Location)> LoadSpecAsync(IServiceProvider services, string source, CancellationToken cancellationToken)
    {
        if (File.Exists(source))
            return (await File.ReadAllTextAsync(source, cancellationToken), Path.GetFullPath(source));
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var client = services.GetRequiredService<IHttpClientSource>().GetClient(new RequestSettings());
            using var response = await client.GetAsync(source, cancellationToken);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync(cancellationToken), source);
        }
        throw new UsageException($"'{source}' is not a file or URL. Pass an OpenAPI / Swagger document (YAML or JSON).");
    }

    /// <summary>JSON or YAML text of an OpenAPI / Swagger document.</summary>
    internal static JsonNode ParseSpec(string text)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        var root = trimmed.StartsWith('{') ? JsonNode.Parse(trimmed) : Yaml.ToJson(trimmed);
        if (root is null || !OpenApi.IsOpenApi(root))
            throw new UsageException("The document is not an OpenAPI or Swagger spec (no 'openapi' or 'swagger' field).");
        return root;
    }
}
