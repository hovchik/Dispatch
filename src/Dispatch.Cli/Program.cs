using System.Text;
using Dispatch.Application.Interop;
using Dispatch.Application.Running;
using Dispatch.Domain;
using Dispatch.Infrastructure;
using Dispatch.Infrastructure.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

public static class Program
{
    public const int ExitOk = 0;
    public const int ExitTestsFailed = 1;
    public const int ExitError = 2;

    private const string Usage = """
        Dispatch CLI - run API collections from the terminal and CI.

        Usage:
          dispatch run <collection> [options]     Run a collection and report results
          dispatch import <file|url>              Import into the Dispatch app (Postman, OpenAPI, HAR, WSDL, .proto, ...)
          dispatch export <collection> [options]  Export a saved collection
          dispatch list                           List saved collections and environments
          dispatch mock <collection> [options]    Serve a collection's examples as a mock server
                                                  (--dynamic: fresh fake data from schemas, --stateful: CRUD memory)
          dispatch load <collection> [options]    Load test a collection
          dispatch docs <collection> [options]    Generate API documentation (--format html|md, --out <file>)
          dispatch scan <collection> [options]    Security-test a collection you are authorised to test
          dispatch flow <collection> [--name n]   Run saved test flow(s) of a collection
          dispatch monitor [--once|--watch] [...]  Run scheduled monitors and send alerts
          dispatch capture [--port 8899] [...]     Record proxied traffic to a HAR or Dispatch collection
          dispatch version

        <collection> is a file (Dispatch, Postman, Insomnia, HAR, OpenAPI, .http), a Dispatch folder, a URL,
        or the name of a collection saved in the app.

        Run options:
          -e, --env <file|name>        Environment to use
          -d, --data <file.csv|json>   Data-driven run: one iteration per row
          -n, --iterations <n>         Number of iterations (default 1)
              --folder <name>          Only run requests in this folder (repeatable)
              --request <name>         Only run requests with this name (repeatable)
              --var <key=value>        Set a variable (repeatable; overrides the environment)
              --delay <ms>             Pause between requests
              --timeout <ms>           Default request timeout
              --bail                   Stop at the first failure
              --insecure               Don't verify TLS certificates
          -r, --reporter <list>        cli, junit, html, json (default cli)
          -o, --out <dir>              Where report files go (default ./dispatch-reports)
              --save-env               Write variables set during the run back to a saved environment
              --update-snapshots       Record the current responses as the new snapshots (and save them)
              --no-color               Plain console output
              --db <path>              Dispatch database (default: the desktop app's)

        Exit codes: 0 all passed, 1 some requests or tests failed, 2 usage or setup error.
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? ExitError : ExitOk;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            var command = args[0];
            var rest = args.Skip(1).ToArray();
            return command switch
            {
                "run" => await RunCommand.ExecuteAsync(rest, cancel.Token),
                "import" => await ImportAsync(rest),
                "export" => await ExportAsync(rest),
                "list" or "ls" => await ListAsync(rest),
                "mock" => await MockCommand.ExecuteAsync(rest, cancel.Token),
                "load" => await LoadCommand.ExecuteAsync(rest, cancel.Token),
                "docs" => await DocsAsync(rest),
                "scan" => await ScanCommand.ExecuteAsync(rest, cancel.Token),
                "flow" => await FlowCommand.ExecuteAsync(rest, cancel.Token),
                "monitor" => await MonitorCommand.ExecuteAsync(rest, cancel.Token),
                "capture" => await CaptureCommand.ExecuteAsync(rest, cancel.Token),
                "version" or "--version" => Version(),
                _ => throw new UsageException($"Unknown command '{command}'. Run 'dispatch --help'.")
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitError;
        }
        catch (Exception ex) when (ex is FormatException or FileNotFoundException or IOException or HttpRequestException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitError;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return ExitError;
        }
    }

    internal static ServiceProvider BuildServices(string? databasePath)
    {
        var services = new ServiceCollection();
        services.AddDispatchCore(databasePath ?? DispatchPaths.DefaultDatabase);
        return services.BuildServiceProvider();
    }

    private static int Version()
    {
        Console.WriteLine($"dispatch {typeof(Program).Assembly.GetName().Version?.ToString(3)}");
        return ExitOk;
    }

    private static async Task<int> ImportAsync(string[] args)
    {
        var parsed = Arguments.Parse(args, new HashSet<string>(), null);
        if (parsed.Positionals.Count == 0)
            throw new UsageException("Usage: dispatch import <file|folder|url> [--db <path>]");
        await using var services = BuildServices(parsed.Option("db"));
        var workspace = new Workspace(services);
        var importer = services.GetRequiredService<Dispatch.Infrastructure.Interop.Importer>();

        foreach (var source in parsed.Positionals)
        {
            var result = source.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(source)
                ? await importer.ImportUrlAsync(source)
                : await importer.ImportPathAsync(source);
            await workspace.SaveImportAsync(result);
            Console.WriteLine($"Imported {result.Format}: {result.Collections.Count} collection(s), {result.RequestCount} request(s), " +
                              $"{result.Environments.Count} environment(s) from {source}");
            foreach (var warning in result.Warnings)
                Console.Error.WriteLine($"warning: {warning}");
        }
        return ExitOk;
    }

    private static async Task<int> ExportAsync(string[] args)
    {
        var parsed = Arguments.Parse(args, new HashSet<string>(), new Dictionary<string, string> { ["o"] = "out", ["f"] = "format" });
        if (parsed.Positionals.Count == 0)
            throw new UsageException("Usage: dispatch export <collection> [--format dispatch|folder|postman|http] [--out <path>]");
        await using var services = BuildServices(parsed.Option("db"));
        var workspace = new Workspace(services);
        var (collection, _) = await workspace.LoadCollectionAsync(parsed.Positionals[0], null);
        var format = (parsed.Option("format") ?? "dispatch").ToLowerInvariant();
        var slug = DispatchFormat.Slug(collection.Name);

        switch (format)
        {
            case "folder":
                var directory = parsed.Option("out") ?? slug;
                DispatchFormat.ExportFolder(collection, directory);
                Console.WriteLine($"Wrote {collection.Requests.Count} request file(s) to {Path.GetFullPath(directory)}");
                return ExitOk;
            case "dispatch" or "postman" or "http":
                var content = format switch
                {
                    "postman" => Postman.Export(collection),
                    "http" => HttpFile.Export(collection),
                    _ => DispatchFormat.ExportCollection(collection)
                };
                var path = parsed.Option("out") ?? format switch
                {
                    "postman" => $"{slug}.postman_collection.json",
                    "http" => $"{slug}.http",
                    _ => $"{slug}.dispatch.json"
                };
                if (path == "-")
                    Console.WriteLine(content);
                else
                {
                    await File.WriteAllTextAsync(path, content);
                    Console.WriteLine($"Wrote {Path.GetFullPath(path)}");
                }
                return ExitOk;
            default:
                throw new UsageException($"Unknown format '{format}'. Use dispatch, folder, postman or http.");
        }
    }

    private static async Task<int> DocsAsync(string[] args)
    {
        var parsed = Arguments.Parse(args, new HashSet<string>(["no-examples", "no-code", "no-tests"]),
            new Dictionary<string, string> { ["o"] = "out", ["f"] = "format" });
        if (parsed.Positionals.Count != 1)
            throw new UsageException("Usage: dispatch docs <collection> [--format html|md] [--out <file>|-] [--no-examples] [--no-code] [--no-tests]");
        await using var services = BuildServices(parsed.Option("db"));
        var (collection, _) = await new Workspace(services).LoadCollectionAsync(parsed.Positionals[0], parsed.Option("collection"));
        var format = (parsed.Option("format") ?? "html").ToLowerInvariant();
        var options = new Dispatch.Application.Docs.DocsOptions
        {
            IncludeExamples = !parsed.Flag("no-examples"),
            IncludeCodeSamples = !parsed.Flag("no-code"),
            IncludeTests = !parsed.Flag("no-tests")
        };
        var content = format switch
        {
            "html" => Dispatch.Application.Docs.DocsGenerator.Html(collection, options),
            "md" or "markdown" => Dispatch.Application.Docs.DocsGenerator.MarkdownText(collection, options),
            _ => throw new UsageException($"Unknown format '{format}'. Use html or md.")
        };
        var path = parsed.Option("out") ?? DispatchFormat.Slug(collection.Name) + (format == "html" ? ".html" : ".md");
        if (path == "-")
        {
            Console.WriteLine(content);
            return ExitOk;
        }
        await File.WriteAllTextAsync(path, content);
        Console.WriteLine($"Wrote documentation for {collection.Requests.Count} endpoint(s) to {Path.GetFullPath(path)}");
        return ExitOk;
    }

    private static async Task<int> ListAsync(string[] args)
    {
        var parsed = Arguments.Parse(args, new HashSet<string>(), null);
        await using var services = BuildServices(parsed.Option("db"));
        var workspace = new Workspace(services);
        Console.WriteLine("Collections:");
        foreach (var c in await workspace.SavedCollectionsAsync())
            Console.WriteLine($"  {c.Name}  ({c.Requests.Count} requests)");
        Console.WriteLine("Environments:");
        foreach (var e in await workspace.SavedEnvironmentsAsync())
            Console.WriteLine($"  {e.Name}  ({e.Variables.Count} variables)");
        return ExitOk;
    }

    /// <summary>Applies --folder / --request filters.</summary>
    internal static List<ApiRequest> Filter(RequestCollection collection, Arguments args)
    {
        var folders = args.RawOptions("folder");
        var names = args.RawOptions("request");
        var requests = collection.Requests.OrderBy(r => r.SortOrder).ToList();
        if (folders.Count > 0)
            requests = requests.Where(r => folders.Any(f => r.Folder.Equals(f, StringComparison.OrdinalIgnoreCase)
                                                            || r.Folder.StartsWith(f.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        if (names.Count > 0)
            requests = requests.Where(r => names.Any(n => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (requests.Count == 0)
            throw new UsageException("No requests match the given filters.");
        return requests;
    }
}
