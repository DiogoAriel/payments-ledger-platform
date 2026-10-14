using Ledger.Application.Abstractions;
using Ledger.Infrastructure.Messaging;
using Ledger.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Wires persistence and messaging from configuration:
    ///   Database:Provider = Postgres | Sqlite   (Sqlite is for local runs and tests)
    ///   ConnectionStrings:Ledger
    ///   Messaging:Kafka:BootstrapServers        (absent = events are logged instead of published)
    /// </summary>
    public static IServiceCollection AddLedgerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Postgres";
        var connectionString = configuration.GetConnectionString("Ledger") ?? "Data Source=ledger.db";

        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            if (connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                // An in-memory SQLite database lives as long as its connection, so one shared
                // connection is kept open for the process (tests use this path).
                var connection = new SqliteConnection(connectionString);
                connection.Open();
                services.AddSingleton(connection);
                services.AddDbContext<LedgerDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()));
            }
            else
            {
                services.AddDbContext<LedgerDbContext>(o => o.UseSqlite(connectionString));
            }
        }
        else
        {
            services.AddDbContext<LedgerDbContext>(o => o.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3)));
        }

        services.AddScoped<AccountRepository>();
        services.AddScoped<IAccountRepository>(sp => sp.GetRequiredService<AccountRepository>());
        services.AddScoped<IJournalRepository, JournalRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IReplayGuard, EfReplayGuard>();
        services.AddScoped<IOutbox, EfOutbox>();

        var kafka = configuration.GetSection(KafkaOptions.SectionName).Get<KafkaOptions>() ?? new KafkaOptions();
        if (!string.IsNullOrWhiteSpace(kafka.BootstrapServers))
        {
            services.AddSingleton(kafka);
            services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        }
        else
        {
            services.AddSingleton<IEventPublisher, LoggingEventPublisher>();
        }

        services.AddHostedService<OutboxPublisherService>();
        return services;
    }
}
