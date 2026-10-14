namespace Ledger.Domain;

/// <summary>
/// Integration events describe facts that already happened in the ledger.
/// They are written to the outbox in the same transaction as the entry and published afterwards,
/// so downstream consumers never see an event for an entry that was rolled back.
/// </summary>
public interface IIntegrationEvent
{
    string EventType { get; }
    DateTimeOffset OccurredAt { get; }
}

public sealed record AccountOpened(
    Guid AccountId,
    string Owner,
    string Kind,
    string Currency,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public string EventType => "ledger.account.opened";
}

public sealed record EntryPosted(
    Guid EntryId,
    string Reference,
    string Description,
    string Currency,
    decimal Amount,
    IReadOnlyList<EntryPostedLine> Lines,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public string EventType => "ledger.entry.posted";

    public static EntryPosted From(JournalEntry entry)
    {
        var currency = entry.Lines[0].Money.Currency.Code;
        var amount = entry.Lines.Where(l => l.Direction == EntryDirection.Debit).Sum(l => l.Money.Amount);
        var lines = entry.Lines
            .Select(l => new EntryPostedLine(l.AccountId.Value, l.Money.Amount, l.Direction.ToString()))
            .ToList();
        return new EntryPosted(entry.Id.Value, entry.Reference, entry.Description, currency, amount, lines, entry.PostedAt);
    }
}

public sealed record EntryPostedLine(Guid AccountId, decimal Amount, string Direction);
