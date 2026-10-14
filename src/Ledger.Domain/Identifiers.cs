namespace Ledger.Domain;

/// <summary>
/// Strongly typed identifiers. A method that takes an AccountId cannot be called with an EntryId by mistake,
/// which a bare Guid would allow. They are records so equality and hashing come for free.
/// </summary>
public readonly record struct AccountId(Guid Value)
{
    public static AccountId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct EntryId(Guid Value)
{
    public static EntryId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}
