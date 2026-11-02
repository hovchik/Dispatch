using Dispatch.Application.Interop;
using Dispatch.Application.Lint;

namespace Dispatch.Cli;

/// <summary><c>dispatch lint</c>: static checks over a collection, no requests sent.</summary>
public static class LintCommand
{
    private const string Usage = "Usage: dispatch lint <collection> [--env e] [--fail-on error|warning|info|never] [--config dispatch-lint.json] " +
                                 "[--disable DP001,DP008] [--enable DP008] [--severity DP004=error] [-r cli,html,json] [-o dir]";

    private static readonly HashSet<string> Flags = ["no-color", "list-rules"];

    public static async Task<int> ExecuteAsync(string[] rawArgs, CancellationToken cancellationToken)
    {
        var args = Arguments.Parse(rawArgs, Flags, new Dictionary<string, string> { ["e"] = "env", ["o"] = "out", ["r"] = "reporter" });
        if (args.Flag("list-rules"))
        {
            foreach (var rule in LintRules.All)
                Console.WriteLine($"{rule.Id}  {rule.DefaultSeverity.ToString().ToLowerInvariant(),-7} {(rule.EnabledByDefault ? "on " : "off")}  {rule.Title}: {rule.Description}");
            return Program.ExitOk;
        }
        SingleRequest.ValidateReporters(args);
        if (args.Positionals.Count != 1)
            throw new UsageException(Usage);
        var failOn = (args.Option("fail-on") ?? "error").ToLowerInvariant();
        LintSeverity? threshold = failOn == "never" ? null : LintConfig.ParseSeverity(failOn) ?? throw new UsageException("--fail-on expects error, warning, info or never.");

        await using var services = Program.BuildServices(args.Option("db"));
        var workspace = new Workspace(services);
        var source = args.Positionals[0];
        var (collection, bundled) = await workspace.LoadCollectionAsync(source, args.Option("collection"));
        var environments = new List<Dispatch.Domain.ApiEnvironment>();
        if (args.Option("env") is { } envName)
            environments.Add(await workspace.LoadEnvironmentAsync(envName, bundled) ?? throw new UsageException($"Environment '{envName}' not found."));
        else
            environments.AddRange(bundled);

        var config = await LoadConfigAsync(args, source, cancellationToken);
        var report = CollectionLinter.Lint(collection, new LintOptions { Config = config, Environments = environments });

        if (args.Options("reporter").DefaultIfEmpty("cli").Contains("cli", StringComparer.OrdinalIgnoreCase))
            Console.WriteLine(LintReportWriter.Text(report, SingleRequest.Color(args)));
        await SingleRequest.WriteReportsAsync(args, $"{DispatchFormat.Slug(collection.Name)}-lint-{report.CreatedAt:yyyyMMdd-HHmmss}",
            reporter => reporter switch
            {
                "html" => (".html", LintReportWriter.Html(report)),
                "json" => (".json", LintReportWriter.Json(report)),
                _ => null
            });

        return threshold is { } t && report.FailsAt(t) ? Program.ExitTestsFailed : Program.ExitOk;
    }

    /// <summary>
    /// --config, else a <c>dispatch-lint.json</c> inside (for a folder) or next to (for a file) the collection, then
    /// --disable / --enable / --severity on top.
    /// </summary>
    private static async Task<LintConfig> LoadConfigAsync(Arguments args, string source, CancellationToken ct)
    {
        var path = args.Option("config");
        if (path is null)
        {
            var candidates = new List<string>();
            if (Directory.Exists(source))
                candidates.Add(Path.Combine(source, LintConfig.FileName));
            else if (File.Exists(source))
                candidates.Add(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, LintConfig.FileName));
            candidates.Add(LintConfig.FileName);
            path = candidates.FirstOrDefault(File.Exists);
        }
        else if (!File.Exists(path))
            throw new UsageException($"--config: {path} does not exist.");

        var config = path is null ? new LintConfig() : LintConfig.Parse(await File.ReadAllTextAsync(path, ct));
        foreach (var id in args.Options("disable"))
            config.Disable(RuleId(id));
        foreach (var id in args.Options("enable"))
            config.Enable(RuleId(id));
        foreach (var pair in args.Options("severity"))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                throw new UsageException($"--severity expects id=level, got '{pair}'.");
            var severity = LintConfig.ParseSeverity(pair[(eq + 1)..]);
            if (severity is null)
                config.Disable(RuleId(pair[..eq]));
            else
                config.Set(RuleId(pair[..eq]), severity.Value);
        }
        return config;
    }

    private static string RuleId(string id) => LintRules.Find(id)?.Id ?? throw new UsageException($"Unknown lint rule '{id}'. Run 'dispatch lint --list-rules'.");
}
