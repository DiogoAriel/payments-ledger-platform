using System.Text.Json;
using Confluent.Kafka;
using Ledger.Application.Abstractions;
using Ledger.Domain;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledger.Infrastructure.Messaging;

/// <summary>Writes the event as a row in the same transaction as the business change.</summary>
public sealed class EfOutbox(LedgerDbContext db) : IOutbox
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task Enqueue(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        db.Outbox.Add(new OutboxMessageRecord
        {
            Id = Guid.NewGuid(),
            EventType = integrationEvent.EventType,
            Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), JsonOptions),
            OccurredAt = integrationEvent.OccurredAt,
        });
        return Task.CompletedTask;
    }
}

/// <summary>Where published events go. Kafka in production; the log in development and tests.</summary>
public interface IEventPublisher
{
    Task Publish(string eventType, string key, string payload, CancellationToken cancellationToken);
}

public sealed class LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public Task Publish(string eventType, string key, string payload, CancellationToken cancellationToken)
    {
        logger.LogInformation("Published {EventType} {Key}: {Payload}", eventType, key, payload);
        return Task.CompletedTask;
    }
}

public sealed class KafkaOptions
{
    public const string SectionName = "Messaging:Kafka";
    public string? BootstrapServers { get; init; }
    public string Topic { get; init; } = "ledger.events";
}

public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;

    public KafkaEventPublisher(KafkaOptions options)
    {
        _topic = options.Topic;
        var config = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            // Idempotent producer plus acks=all: a retry never duplicates, and a message is only
            // confirmed once every in-sync replica has it. Ordering per key is preserved.
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageSendMaxRetries = 5,
        };
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task Publish(string eventType, string key, string payload, CancellationToken cancellationToken)
    {
        var message = new Message<string, string>
        {
            Key = key,
            Value = payload,
            Headers = new Headers { { "event-type", System.Text.Encoding.UTF8.GetBytes(eventType) } },
        };
        await _producer.ProduceAsync(_topic, message, cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}

/// <summary>
/// Relays outbox rows to the publisher. Runs in the API process; at-least-once delivery, so consumers
/// must be idempotent (every event carries the entry id). Failed rows keep their error and are retried.
/// </summary>
public sealed class OutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    IEventPublisher publisher,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishPending(stoppingToken);
                if (published == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox publishing loop failed; retrying after {Interval}", PollInterval);
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    internal async Task<int> PublishPending(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        var pending = await db.Outbox
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            try
            {
                await publisher.Publish(message.EventType, message.Id.ToString(), message.Payload, cancellationToken);
                message.PublishedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                logger.LogWarning(ex, "Failed to publish outbox message {MessageId} (attempt {Attempts})", message.Id, message.Attempts);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return pending.Count(m => m.PublishedAt is not null);
    }
}
