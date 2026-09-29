using Dispatch.Application.Abstractions;
using Dispatch.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Infrastructure.Persistence;

// Repositories create a short-lived DbContext per operation: the recommended pattern for
// desktop apps, where a long-lived context would accumulate tracked entities and stale state.

public sealed class CollectionRepository(IDbContextFactory<DispatchDbContext> factory) : ICollectionRepository
{
    public async Task<IReadOnlyList<RequestCollection>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var collections = await db.Collections
            .AsNoTracking()
            .Include(c => c.Requests)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);

        foreach (var c in collections)
            c.Requests = c.Requests.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToList();
        return collections;
    }

    public async Task AddAsync(RequestCollection collection, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        collection.SortOrder = (await db.Collections.MaxAsync(c => (int?)c.SortOrder, ct) ?? -1) + 1;
        db.Collections.Add(collection);
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(Guid collectionId, string name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Collections
            .Where(c => c.Id == collectionId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, name), ct);
    }

    public async Task DeleteAsync(Guid collectionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Requests.Where(r => r.CollectionId == collectionId).ExecuteDeleteAsync(ct);
        await db.Collections.Where(c => c.Id == collectionId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task SaveRequestAsync(ApiRequest request, CancellationToken ct = default)
    {
        if (request.CollectionId is null)
            throw new InvalidOperationException("A request must belong to a collection to be saved.");

        await using var db = await factory.CreateDbContextAsync(ct);
        request.UpdatedAt = DateTimeOffset.UtcNow;

        var exists = await db.Requests.AnyAsync(r => r.Id == request.Id, ct);
        if (exists)
        {
            db.Requests.Update(request);
        }
        else
        {
            request.SortOrder = (await db.Requests
                .Where(r => r.CollectionId == request.CollectionId)
                .MaxAsync(r => (int?)r.SortOrder, ct) ?? -1) + 1;
            db.Requests.Add(request);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Requests.Where(r => r.Id == requestId).ExecuteDeleteAsync(ct);
    }
}

public sealed class EnvironmentRepository(IDbContextFactory<DispatchDbContext> factory) : IEnvironmentRepository
{
    public async Task<IReadOnlyList<ApiEnvironment>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Environments.AsNoTracking().OrderBy(e => e.Name).ToListAsync(ct);
    }

    public async Task SaveAsync(ApiEnvironment environment, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Environments.AnyAsync(e => e.Id == environment.Id, ct))
            db.Environments.Update(environment);
        else
            db.Environments.Add(environment);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Environments.Where(e => e.Id == environmentId).ExecuteDeleteAsync(ct);
    }
}

public sealed class HistoryRepository(IDbContextFactory<DispatchDbContext> factory) : IHistoryRepository
{
    /// <summary>Oldest entries beyond this count are pruned on insert.</summary>
    public const int MaxEntries = 500;

    public async Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.History.AsNoTracking()
            .OrderByDescending(h => h.Timestamp)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task AddAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.History.Add(entry);
        await db.SaveChangesAsync(ct);

        var stale = db.History.OrderByDescending(h => h.Timestamp).Skip(MaxEntries).Select(h => h.Id);
        await db.History.Where(h => stale.Contains(h.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteAsync(Guid entryId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.History.Where(h => h.Id == entryId).ExecuteDeleteAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.History.ExecuteDeleteAsync(ct);
    }
}

public sealed class SettingsRepository(IDbContextFactory<DispatchDbContext> factory) : ISettingsRepository
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Settings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entry = await db.Settings.FindAsync([key], ct);
        if (entry is null)
            db.Settings.Add(new SettingEntry { Key = key, Value = value });
        else
            entry.Value = value;
        await db.SaveChangesAsync(ct);
    }
}
