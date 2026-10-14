using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Persistence model. These records mirror the tables, not the domain: the domain objects are
/// rebuilt from them by the repositories. Keeping the two apart means EF conventions never leak
/// into business rules and the domain stays free of persistence attributes.
/// </summary>
public sealed class AccountRecord
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = string.Empty;
    public int Kind { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public long Version { get; set; }
}

public sealed class JournalEntryRecord
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset PostedAt { get; set; }
    public List<EntryLineRecord> Lines { get; set; } = new();
}

public sealed class EntryLineRecord
{
    public long Id { get; set; }
    public Guid EntryId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int Direction { get; set; }
}

public sealed class OutboxMessageRecord
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class WebhookNonceRecord
{
    public string Provider { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<AccountRecord> Accounts => Set<AccountRecord>();
    public DbSet<JournalEntryRecord> JournalEntries => Set<JournalEntryRecord>();
    public DbSet<EntryLineRecord> EntryLines => Set<EntryLineRecord>();
    public DbSet<OutboxMessageRecord> Outbox => Set<OutboxMessageRecord>();
    public DbSet<WebhookNonceRecord> WebhookNonces => Set<WebhookNonceRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountRecord>(b =>
        {
            b.ToTable("accounts");
            b.HasKey(a => a.Id);
            b.Property(a => a.Owner).HasMaxLength(120).IsRequired();
            b.Property(a => a.Currency).HasMaxLength(3).IsRequired();
            b.Property(a => a.Balance).HasPrecision(18, 4);
            // Optimistic concurrency: an update only succeeds if Version is still what we read.
            b.Property(a => a.Version).IsConcurrencyToken();
            b.HasIndex(a => new { a.Kind, a.Owner, a.Currency });
        });

        modelBuilder.Entity<JournalEntryRecord>(b =>
        {
            b.ToTable("journal_entries");
            b.HasKey(e => e.Id);
            b.Property(e => e.Reference).HasMaxLength(128).IsRequired();
            b.Property(e => e.Description).HasMaxLength(200).IsRequired();
            // The reference is the idempotency key: the database enforces "never post twice".
            b.HasIndex(e => e.Reference).IsUnique();
            b.HasMany(e => e.Lines).WithOne().HasForeignKey(l => l.EntryId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(e => e.Lines).AutoInclude();
        });

        modelBuilder.Entity<EntryLineRecord>(b =>
        {
            b.ToTable("entry_lines");
            b.HasKey(l => l.Id);
            b.Property(l => l.Amount).HasPrecision(18, 4);
            b.Property(l => l.Currency).HasMaxLength(3).IsRequired();
            b.HasIndex(l => l.AccountId);
        });

        modelBuilder.Entity<OutboxMessageRecord>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(m => m.Id);
            b.Property(m => m.EventType).HasMaxLength(100).IsRequired();
            b.Property(m => m.LastError).HasMaxLength(2000);
            b.HasIndex(m => m.PublishedAt);
        });

        modelBuilder.Entity<WebhookNonceRecord>(b =>
        {
            b.ToTable("webhook_nonces");
            b.HasKey(n => new { n.Provider, n.Nonce });
            b.Property(n => n.Provider).HasMaxLength(40);
            b.Property(n => n.Nonce).HasMaxLength(128);
            b.HasIndex(n => n.ExpiresAt);
        });

        if (Database.IsSqlite())
        {
            // SQLite stores DateTimeOffset as text and cannot order or compare it in SQL.
            // Every timestamp here is UTC, so a binary (ticks) representation keeps ordering correct.
            // PostgreSQL is unaffected and keeps native timestamptz columns.
            foreach (var property in modelBuilder.Model.GetEntityTypes()
                         .SelectMany(e => e.GetProperties())
                         .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
            }
        }
    }
}
