using Dispatch.Application.Abstractions;
using Dispatch.Application.Requests;
using Dispatch.Infrastructure.Auth;
using Dispatch.Infrastructure.Http;
using Dispatch.Infrastructure.Interop;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Protocols;
using Dispatch.Infrastructure.Scripting;
using Dispatch.Infrastructure.Security;
using Dispatch.Infrastructure.Protocols.Grpc;
using Dispatch.Infrastructure.Protocols.Messaging;
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
        services.AddSingleton<ISecretProtector>(_ => new SecretProtector(Path.GetDirectoryName(Path.GetFullPath(databasePath))!));
        services.AddSingleton<ICollectionRepository, CollectionRepository>();
        services.AddSingleton<IEnvironmentRepository, EnvironmentRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();
        services.AddSingleton<IFlowRepository, FlowRepository>();
        services.AddSingleton<IMonitorRepository, MonitorRepository>();
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
        services.AddSingleton<SystemBrowserInteraction>();
        services.AddSingleton<IOAuth2Interaction>(sp => sp.GetRequiredService<SystemBrowserInteraction>());
        services.AddSingleton<IOAuth2TokenProvider, OAuth2TokenProvider>();
        services.AddSingleton<IScriptRunner, JintScriptRunner>();
        services.AddSingleton<IContractValidator, OpenApiContractValidator>();
        services.AddSingleton<Importer>();

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
        services.AddSingleton<WsdlLoader>();
        services.AddSingleton<IProtocolExecutor, SoapExecutor>();
        services.AddSingleton<GrpcSchemaProvider>();
        services.AddSingleton<IProtocolExecutor, GrpcExecutor>();
        services.AddSingleton<IProtocolExecutor, MqttExecutor>();
        services.AddSingleton<IProtocolExecutor, KafkaExecutor>();
        services.AddSingleton<IProtocolExecutor, AmqpExecutor>();
        services.AddSingleton<IProtocolExecutor, SocketExecutor>();

        services.AddSingleton<IRequestSender, RequestSender>();
        services.AddSingleton<Dispatch.Application.Running.CollectionRunner>();
        services.AddSingleton<Dispatch.Application.Load.LoadTester>();
        services.AddSingleton<Dispatch.Application.Security.SecurityScanner>();
        services.AddSingleton<Dispatch.Application.Flows.FlowRunner>();
        services.AddSingleton<Dispatch.Application.Monitoring.IAlertSender, Monitoring.AlertSender>();
        services.AddSingleton<Dispatch.Application.Monitoring.MonitorService>();
        services.AddSingleton<Capture.CertificateAuthority>();
        services.AddTransient<Capture.CaptureProxy>();
        return services;
    }
}
