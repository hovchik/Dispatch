using Dispatch.Application.Abstractions;
using Dispatch.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Infrastructure.Persistence;

// Repositories create a short-lived DbContext per operation: the recommended pattern for
// desktop apps, where a long-lived context would accumulate tracked entities and stale state.

/// <summary>Encrypts / decrypts the values of variables marked secret, when a protector is configured.</summary>
internal static class SecretVariables
{
    public static List<KeyValueItem> Protect(IEnumerable<KeyValueItem> variables, ISecretProtector? protector) =>
        variables.Select(v =>
        {
            var copy = v.Clone();
            if (protector is not null && v.IsSecret)
                copy.Value = protector.Protect(v.Value);
            return copy;
        }).ToList();

    public static void Unprotect(List<KeyValueItem> variables, ISecretProtector? protector)
    {
        if (protector is null)
            return;
        foreach (var v in variables.Where(v => v.IsSecret))
        {
            try
            {
                v.Value = protector.Unprotect(v.Value);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                // Encrypted with a key we no longer have (e.g. copied from another machine): the user must re-enter it.
                v.Value = "";
            }
        }
    }
}

public sealed class CollectionRepository(IDbContextFactory<DispatchDbContext> factory, ISecretProtector? secrets = null) : ICollectionRepository
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
        {
            c.Requests = c.Requests.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToList();
            SecretVariables.Unprotect(c.Variables, secrets);
        }
        return collections;
    }

    public async Task AddAsync(RequestCollection collection, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        collection.SortOrder = (await db.Collections.MaxAsync(c => (int?)c.SortOrder, ct) ?? -1) + 1;
        var plainVariables = collection.Variables;
        collection.Variables = SecretVariables.Protect(plainVariables, secrets);
        try
        {
            db.Collections.Add(collection);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            collection.Variables = plainVariables;
        }
    }

    public async Task RenameAsync(Guid collectionId, string name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Collections
            .Where(c => c.Id == collectionId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, name), ct);
    }

    public async Task UpdateAsync(RequestCollection collection, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.Collections.FirstOrDefaultAsync(c => c.Id == collection.Id, ct)
                       ?? throw new InvalidOperationException("Collection not found.");
        existing.Name = collection.Name;
        existing.Description = collection.Description;
        existing.Variables = SecretVariables.Protect(collection.Variables, secrets);
        existing.SpecLocation = collection.SpecLocation;
        await db.SaveChangesAsync(ct);
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

public sealed class EnvironmentRepository(IDbContextFactory<DispatchDbContext> factory, ISecretProtector? secrets = null) : IEnvironmentRepository
{
    public async Task<IReadOnlyList<ApiEnvironment>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var environments = await db.Environments.AsNoTracking().OrderBy(e => e.Name).ToListAsync(ct);
        foreach (var environment in environments)
            SecretVariables.Unprotect(environment.Variables, secrets);
        return environments;
    }

    public async Task SaveAsync(ApiEnvironment environment, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Store an encrypted copy; the caller keeps working with plain values.
        var stored = new ApiEnvironment
        {
            Id = environment.Id,
            Name = environment.Name,
            Variables = SecretVariables.Protect(environment.Variables, secrets)
        };
        if (await db.Environments.AnyAsync(e => e.Id == environment.Id, ct))
            db.Environments.Update(stored);
        else
            db.Environments.Add(stored);
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

public sealed class FlowRepository(IDbContextFactory<DispatchDbContext> factory) : IFlowRepository
{
    public async Task<IReadOnlyList<TestFlow>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Flows.AsNoTracking().OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync(ct);
    }

    public async Task SaveAsync(TestFlow flow, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        if (await db.Flows.AnyAsync(f => f.Id == flow.Id, ct))
            db.Flows.Update(flow);
        else
        {
            flow.SortOrder = (await db.Flows.MaxAsync(f => (int?)f.SortOrder, ct) ?? -1) + 1;
            db.Flows.Add(flow);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid flowId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Flows.Where(f => f.Id == flowId).ExecuteDeleteAsync(ct);
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
