using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Protocols;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers application services, protocol executors and SQLite persistence.</summary>
    public static IServiceCollection AddDispatchCore(this IServiceCollection services, string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

        services.AddDbContextFactory<DispatchDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<ICollectionRepository, CollectionRepository>();
        services.AddSingleton<IEnvironmentRepository, EnvironmentRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();

        services.AddDispatchEngine();
        return services;
    }

    /// <summary>Everything needed to send requests (no persistence): used by the app and the CLI.</summary>
    public static IServiceCollection AddDispatchEngine(this IServiceCollection services)
    {
        services.AddSingleton<HttpClientPool>();
        services.AddSingleton<IHttpClientSource>(sp => sp.GetRequiredService<HttpClientPool>());
        services.AddSingleton<CookieJar>();
        services.AddSingleton<ICookieJar>(sp => sp.GetRequiredService<CookieJar>());
        services.AddSingleton<SessionVariables>();

        services.AddSingleton<IRequestExecutor, HttpRequestExecutor>();
        services.AddSingleton<IRequestMessageBuilder, RequestMessageBuilder>();
        services.AddSingleton<HttpProtocolExecutor>();
        services.AddSingleton<IProtocolExecutor>(sp => sp.GetRequiredService<HttpProtocolExecutor>());

        services.AddSingleton<WebSocketConnector>();
        services.AddSingleton<GraphQlExecutor>();
        services.AddSingleton<IProtocolExecutor>(sp => sp.GetRequiredService<GraphQlExecutor>());
        services.AddSingleton<IProtocolExecutor, WebSocketExecutor>();
        services.AddSingleton<IProtocolExecutor, SseExecutor>();
        services.AddSingleton<IProtocolExecutor, SocketIoExecutor>();

        services.AddSingleton<IRequestSender, RequestSender>();
        return services;
    }
}
