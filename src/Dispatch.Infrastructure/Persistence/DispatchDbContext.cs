using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Dispatch.Infrastructure.Persistence;

public sealed class SettingEntry
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}

public sealed class DispatchDbContext(DbContextOptions<DispatchDbContext> options) : DbContext(options)
{
    public DbSet<RequestCollection> Collections => Set<RequestCollection>();
    public DbSet<ApiRequest> Requests => Set<ApiRequest>();
    public DbSet<ApiEnvironment> Environments => Set<ApiEnvironment>();
    public DbSet<HistoryEntry> History => Set<HistoryEntry>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot ORDER BY / compare DateTimeOffset natively; store it as a sortable long.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RequestCollection>(b =>
        {
            b.ToTable("Collections");
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).IsRequired().HasMaxLength(200);
            b.HasMany(c => c.Requests)
                .WithOne()
                .HasForeignKey(r => r.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiRequest>(b =>
        {
            b.ToTable("Requests");
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).IsRequired().HasMaxLength(200);
            b.Property(r => r.Url).IsRequired();
            b.Property(r => r.Method).HasConversion<string>().HasMaxLength(10);
            b.Property(r => r.QueryParams).HasJsonConversion();
            b.Property(r => r.Headers).HasJsonConversion();
            b.Property(r => r.Body).HasJsonConversion();
            b.Property(r => r.Auth).HasJsonConversion();
            b.HasIndex(r => r.CollectionId);
        });

        modelBuilder.Entity<ApiEnvironment>(b =>
        {
            b.ToTable("Environments");
            b.HasKey(e => e.Id);
            b.Property(e => e.Name).IsRequired().HasMaxLength(200);
            b.Property(e => e.Variables).HasJsonConversion();
        });

        modelBuilder.Entity<HistoryEntry>(b =>
        {
            b.ToTable("History");
            b.HasKey(h => h.Id);
            b.Property(h => h.Method).HasConversion<string>().HasMaxLength(10);
            b.Property(h => h.Request).HasJsonConversion();
            b.HasIndex(h => h.Timestamp);
        });

        modelBuilder.Entity<SettingEntry>(b =>
        {
            b.ToTable("Settings");
            b.HasKey(s => s.Key);
        });
    }
}

internal static class JsonColumn
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal static T Deserialize<T>(string json) where T : class, new() =>
        JsonSerializer.Deserialize<T>(json, Options) ?? new T();

    /// <summary>Stores a complex value as a JSON TEXT column, with a comparer so EF detects in-place edits.</summary>
    internal static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property) where T : class, new()
    {
        var converter = new ValueConverter<T, string>(
            v => Serialize(v),
            s => Deserialize<T>(s));

        var comparer = new ValueComparer<T>(
            (a, b) => Serialize(a) == Serialize(b),
            v => Serialize(v).GetHashCode(),
            v => Deserialize<T>(Serialize(v)));

        property.HasConversion(converter, comparer).HasColumnType("TEXT").IsRequired();
        return property;
    }
}
