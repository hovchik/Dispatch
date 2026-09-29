using Dispatch.Application.Abstractions;
using Dispatch.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Infrastructure.Persistence;

public sealed class DatabaseInitializer(IDbContextFactory<DispatchDbContext> factory)
{
    private const string SeededKey = "seeded";

    /// <summary>Creates the schema on first run and adds a small "Getting started" collection.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);

        if (await db.Settings.AnyAsync(s => s.Key == SeededKey, ct))
            return;

        var environment = new ApiEnvironment
        {
            Name = "httpbin",
            Variables =
            [
                new("baseUrl", "https://httpbin.org"),
                new("token", "my-secret-token")
            ]
        };
        db.Environments.Add(environment);
        db.Settings.Add(new SettingEntry { Key = SettingKeys.ActiveEnvironmentId, Value = environment.Id.ToString() });

        db.Collections.Add(new RequestCollection
        {
            Name = "Getting Started",
            Requests =
            [
                new ApiRequest
                {
                    Name = "Get with query params",
                    Method = HttpVerb.Get,
                    Url = "{{baseUrl}}/get?page=1&size=20",
                    QueryParams = [new("page", "1"), new("size", "20")],
                    SortOrder = 0
                },
                new ApiRequest
                {
                    Name = "Create (JSON body)",
                    Method = HttpVerb.Post,
                    Url = "{{baseUrl}}/post",
                    Body = new RequestBody
                    {
                        Mode = BodyMode.Json,
                        Content = """
                                  {
                                    "name": "Dispatch",
                                    "active": true,
                                    "tags": ["api", "client"]
                                  }
                                  """
                    },
                    SortOrder = 1
                },
                new ApiRequest
                {
                    Name = "Bearer auth",
                    Method = HttpVerb.Get,
                    Url = "{{baseUrl}}/bearer",
                    Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "{{token}}" },
                    SortOrder = 2
                }
            ]
        });

        db.Settings.Add(new SettingEntry { Key = SeededKey, Value = "1" });
        await db.SaveChangesAsync(ct);
    }
}
