using Dispatch.Application.Abstractions;
using Dispatch.Application.Interop;
using Dispatch.Domain;
using Dispatch.Infrastructure.Interop;
using Dispatch.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Cli;

/// <summary>Resolves collections and environments from files, URLs, or the Dispatch database.</summary>
public sealed class Workspace(IServiceProvider services)
{
    private readonly Importer _importer = services.GetRequiredService<Importer>();
    private bool _databaseReady;

    private async Task EnsureDatabaseAsync()
    {
        if (_databaseReady)
            return;
        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        _databaseReady = true;
    }

    /// <summary>A file, folder or URL to import, or the name of a collection saved in the app.</summary>
    public async Task<(RequestCollection Collection, IReadOnlyList<ApiEnvironment> BundledEnvironments)> LoadCollectionAsync(
        string source, string? collectionName)
    {
        if (File.Exists(source) || Directory.Exists(source) || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var result = File.Exists(source) || Directory.Exists(source)
                ? await _importer.ImportPathAsync(source)
                : await _importer.ImportUrlAsync(source);
            foreach (var warning in result.Warnings)
                Console.Error.WriteLine($"warning: {warning}");
            if (result.Collections.Count == 0)
                throw new UsageException($"{source} contains no collection ({result.Format}).");
            var collection = collectionName is null
                ? result.Collections[0]
                : result.Collections.FirstOrDefault(c => c.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase))
                  ?? throw new UsageException($"No collection named '{collectionName}' in {source}.");
            return (collection, result.Environments);
        }

        await EnsureDatabaseAsync();
        var saved = await services.GetRequiredService<ICollectionRepository>().GetAllAsync();
        var match = saved.FirstOrDefault(c => c.Name.Equals(source, StringComparison.OrdinalIgnoreCase))
                    ?? throw new UsageException($"'{source}' is not a file, folder, URL or saved collection. " +
                                                $"Saved collections: {(saved.Count == 0 ? "(none)" : string.Join(", ", saved.Select(c => c.Name)))}");
        return (match, []);
    }

    public async Task<ApiEnvironment?> LoadEnvironmentAsync(string? source, IReadOnlyList<ApiEnvironment> bundled)
    {
        if (source is null)
            return null;
        if (File.Exists(source))
        {
            var result = await _importer.ImportPathAsync(source);
            return result.Environments.FirstOrDefault() ?? throw new UsageException($"{source} contains no environment.");
        }
        var fromBundle = bundled.FirstOrDefault(e => e.Name.Equals(source, StringComparison.OrdinalIgnoreCase));
        if (fromBundle is not null)
            return fromBundle;

        await EnsureDatabaseAsync();
        var saved = await services.GetRequiredService<IEnvironmentRepository>().GetAllAsync();
        return saved.FirstOrDefault(e => e.Name.Equals(source, StringComparison.OrdinalIgnoreCase))
               ?? throw new UsageException($"Environment '{source}' not found. Pass a file, or one of: " +
                                           string.Join(", ", bundled.Select(e => e.Name).Concat(saved.Select(e => e.Name)).DefaultIfEmpty("(none)")));
    }

    public async Task<IReadOnlyList<RequestCollection>> SavedCollectionsAsync()
    {
        await EnsureDatabaseAsync();
        return await services.GetRequiredService<ICollectionRepository>().GetAllAsync();
    }

    public async Task<IReadOnlyList<ApiEnvironment>> SavedEnvironmentsAsync()
    {
        await EnsureDatabaseAsync();
        return await services.GetRequiredService<IEnvironmentRepository>().GetAllAsync();
    }

    public async Task SaveImportAsync(ImportResult result)
    {
        await EnsureDatabaseAsync();
        var collections = services.GetRequiredService<ICollectionRepository>();
        var environments = services.GetRequiredService<IEnvironmentRepository>();
        foreach (var collection in result.Collections)
        {
            var requests = collection.Requests.ToList();
            collection.Id = Guid.NewGuid();
            collection.Requests = [];
            await collections.AddAsync(collection);
            RequestIdentity.Reassign(requests);
            foreach (var request in requests)
            {
                request.CollectionId = collection.Id;
                await collections.SaveRequestAsync(request);
            }
        }
        foreach (var environment in result.Environments)
        {
            environment.Id = Guid.NewGuid();
            await environments.SaveAsync(environment);
        }
    }

    /// <summary>
    /// Writes changed requests back to where the collection came from: the app database, a Dispatch file or a Dispatch
    /// folder. Other formats (Postman, OpenAPI, ...) are left untouched and a Dispatch file is written next to them.
    /// </summary>
    public async Task<string> SaveCollectionAsync(RequestCollection collection, string source, IReadOnlyList<ApiEnvironment> bundled,
        IEnumerable<Guid> changedRequests)
    {
        if (Directory.Exists(source))
        {
            DispatchFormat.ExportFolder(collection, source, bundled);
            return Path.GetFullPath(source);
        }
        if (File.Exists(source))
        {
            var isDispatch = false;
            try
            {
                isDispatch = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(source)) is { } root && DispatchFormat.IsDispatchJson(root);
            }
            catch (System.Text.Json.JsonException)
            {
            }
            var target = isDispatch ? source : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, DispatchFormat.Slug(collection.Name) + ".dispatch.json");
            await File.WriteAllTextAsync(target, DispatchFormat.ExportCollection(collection, bundled));
            return Path.GetFullPath(target);
        }
        if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new UsageException("Cannot write snapshots back to a URL; download the collection first.");

        await EnsureDatabaseAsync();
        var repository = services.GetRequiredService<ICollectionRepository>();
        var changed = changedRequests.ToHashSet();
        foreach (var request in collection.Requests.Where(r => changed.Contains(r.Id)))
            await repository.SaveRequestAsync(request);
        return $"saved collection '{collection.Name}'";
    }

    public async Task SaveEnvironmentAsync(ApiEnvironment environment)
    {
        await EnsureDatabaseAsync();
        await services.GetRequiredService<IEnvironmentRepository>().SaveAsync(environment);
    }
}
