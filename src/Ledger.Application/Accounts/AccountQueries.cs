using FluentValidation;
using Ledger.Application.Abstractions;
using Ledger.Domain;

namespace Ledger.Application.Accounts;

public sealed record GetAccountQuery(Guid AccountId) : IQuery<AccountDto?>;

public sealed class GetAccountHandler(IAccountRepository accounts) : IQueryHandler<GetAccountQuery, AccountDto?>
{
    public async Task<AccountDto?> Handle(GetAccountQuery query, CancellationToken cancellationToken)
    {
        var account = await accounts.Find(new AccountId(query.AccountId), cancellationToken);
        return account is null ? null : AccountDto.From(account);
    }
}

public sealed record GetStatementQuery(Guid AccountId, int Limit = 50) : IQuery<StatementDto?>;

public sealed record StatementDto(AccountDto Account, IReadOnlyList<StatementLineDto> Entries);

public sealed record StatementLineDto(Guid EntryId, string Reference, string Description, DateTimeOffset PostedAt, string Direction, decimal Amount);

public sealed class GetStatementValidator : AbstractValidator<GetStatementQuery>
{
    public GetStatementValidator()
    {
        RuleFor(q => q.Limit).InclusiveBetween(1, 500);
    }
}

public sealed class GetStatementHandler(IAccountRepository accounts, IJournalRepository journal)
    : IQueryHandler<GetStatementQuery, StatementDto?>
{
    public async Task<StatementDto?> Handle(GetStatementQuery query, CancellationToken cancellationToken)
    {
        var accountId = new AccountId(query.AccountId);
        var account = await accounts.Find(accountId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var entries = await journal.ListByAccount(accountId, query.Limit, cancellationToken);
        var lines = entries
            .SelectMany(e => e.Lines
                .Where(l => l.AccountId == accountId)
                .Select(l => new StatementLineDto(e.Id.Value, e.Reference, e.Description, e.PostedAt, l.Direction.ToString(), l.Money.Amount)))
            .OrderByDescending(l => l.PostedAt)
            .ToList();

        return new StatementDto(AccountDto.From(account), lines);
    }
}
