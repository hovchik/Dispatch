using Dispatch.Domain;

namespace Dispatch.Application.Abstractions;

/// <summary>Sends a fully built HTTP request and captures the response.</summary>
public interface IRequestExecutor
{
    Task<ApiResponse> ExecuteAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

public interface ICollectionRepository
{
    Task<IReadOnlyList<RequestCollection>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(RequestCollection collection, CancellationToken ct = default);
    Task RenameAsync(Guid collectionId, string name, CancellationToken ct = default);
    Task DeleteAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>Inserts or updates a request. The request must belong to a collection.</summary>
    Task SaveRequestAsync(ApiRequest request, CancellationToken ct = default);
    Task DeleteRequestAsync(Guid requestId, CancellationToken ct = default);
}

public interface IEnvironmentRepository
{
    Task<IReadOnlyList<ApiEnvironment>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(ApiEnvironment environment, CancellationToken ct = default);
    Task DeleteAsync(Guid environmentId, CancellationToken ct = default);
}

public interface IHistoryRepository
{
    Task<IReadOnlyList<HistoryEntry>> GetRecentAsync(int take, CancellationToken ct = default);
    Task AddAsync(HistoryEntry entry, CancellationToken ct = default);
    Task DeleteAsync(Guid entryId, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>Small key/value store for app preferences (active environment, theme, ...).</summary>
public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, CancellationToken ct = default);
}

public static class SettingKeys
{
    public const string ActiveEnvironmentId = "activeEnvironmentId";
    public const string Theme = "theme";
}
