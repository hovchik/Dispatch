using System.Net;
using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers application services, HTTP pipeline and SQLite persistence.</summary>
    public static IServiceCollection AddDispatchCore(this IServiceCollection services, string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

        services.AddDbContextFactory<DispatchDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<ICollectionRepository, CollectionRepository>();
        services.AddSingleton<IEnvironmentRepository, EnvironmentRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();

        services.AddHttpClient(HttpRequestExecutor.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(100);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                // An API client must send exactly the headers the user configured, not a shared cookie jar.
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(30)
            });

        services.AddSingleton<IRequestExecutor, HttpRequestExecutor>();
        services.AddSingleton<IRequestMessageBuilder, RequestMessageBuilder>();
        services.AddSingleton<IRequestSender, RequestSender>();
        return services;
    }
}
