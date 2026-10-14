using FluentValidation;
using Ledger.Application.Abstractions;
using Ledger.Domain;

namespace Ledger.Application.Accounts;

public sealed record OpenAccountCommand(string Owner, AccountKind Kind, string Currency) : ICommand<AccountDto>;

public sealed record AccountDto(Guid Id, string Owner, string Kind, string Currency, decimal Balance, DateTimeOffset OpenedAt)
{
    public static AccountDto From(Account account) =>
        new(account.Id.Value, account.Owner, account.Kind.ToString(), account.Currency.Code, account.Balance.Amount, account.OpenedAt);
}

public sealed class OpenAccountValidator : AbstractValidator<OpenAccountCommand>
{
    public OpenAccountValidator()
    {
        RuleFor(c => c.Owner).NotEmpty().MaximumLength(120);
        RuleFor(c => c.Kind).IsInEnum();
        RuleFor(c => c.Currency).NotEmpty().Length(3);
    }
}

public sealed class OpenAccountHandler(
    IAccountRepository accounts,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock) : ICommandHandler<OpenAccountCommand, AccountDto>
{
    public async Task<AccountDto> Handle(OpenAccountCommand command, CancellationToken cancellationToken)
    {
        var account = Account.Open(command.Owner, command.Kind, Currency.FromCode(command.Currency), clock.UtcNow);

        await accounts.Add(account, cancellationToken);
        await outbox.Enqueue(new AccountOpened(account.Id.Value, account.Owner, account.Kind.ToString(), account.Currency.Code, clock.UtcNow), cancellationToken);
        await unitOfWork.Commit(cancellationToken);

        return AccountDto.From(account);
    }
}
