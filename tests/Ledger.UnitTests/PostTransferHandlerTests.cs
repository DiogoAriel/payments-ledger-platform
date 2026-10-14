using FluentAssertions;
using Ledger.Application.Transfers;
using Ledger.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledger.UnitTests;

public class PostTransferHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryJournalRepository _journal = new();
    private readonly InMemoryOutbox _outbox = new();
    private readonly CountingUnitOfWork _unitOfWork = new();
    private readonly PostTransferHandler _handler;
    private readonly Account _settlement;
    private readonly Account _alice;

    public PostTransferHandlerTests()
    {
        _handler = new PostTransferHandler(_accounts, _journal, _outbox, _unitOfWork, new FixedClock(Now), NullLogger<PostTransferHandler>.Instance);
        _settlement = Account.Open("acme-pay", AccountKind.ProviderSettlement, Currency.BRL, Now);
        _alice = Account.Open("alice", AccountKind.Customer, Currency.BRL, Now);
        _accounts.Accounts[_settlement.Id] = _settlement;
        _accounts.Accounts[_alice.Id] = _alice;
    }

    [Fact]
    public async Task Posts_the_entry_the_event_and_commits_once()
    {
        var command = new PostTransferCommand("ref-1", _settlement.Id.Value, _alice.Id.Value, 250m, "BRL", "top-up");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.AlreadyPosted.Should().BeFalse();
        _alice.Balance.Should().Be(Money.Of(250m, "BRL"));
        _journal.Entries.Should().ContainSingle(e => e.Reference == "ref-1");
        _outbox.Events.Should().ContainSingle().Which.Should().BeOfType<EntryPosted>()
            .Which.Amount.Should().Be(250m);
        _unitOfWork.Commits.Should().Be(1);
    }

    [Fact]
    public async Task Same_reference_is_idempotent_and_does_not_post_again()
    {
        var command = new PostTransferCommand("ref-2", _settlement.Id.Value, _alice.Id.Value, 100m, "BRL", "top-up");

        var first = await _handler.Handle(command, CancellationToken.None);
        var second = await _handler.Handle(command, CancellationToken.None);

        second.AlreadyPosted.Should().BeTrue();
        second.EntryId.Should().Be(first.EntryId);
        _alice.Balance.Should().Be(Money.Of(100m, "BRL"), "the replay must not move money twice");
        _journal.Entries.Should().HaveCount(1);
        _unitOfWork.Commits.Should().Be(1);
    }

    [Fact]
    public async Task Insufficient_funds_fail_closed_with_nothing_committed()
    {
        var bob = Account.Open("bob", AccountKind.Customer, Currency.BRL, Now);
        _accounts.Accounts[bob.Id] = bob;
        var command = new PostTransferCommand("ref-3", _alice.Id.Value, bob.Id.Value, 10m, "BRL", "p2p");

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InsufficientFundsException>();
        _journal.Entries.Should().BeEmpty();
        _outbox.Events.Should().BeEmpty();
        _unitOfWork.Commits.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_account_is_reported_as_not_found()
    {
        var command = new PostTransferCommand("ref-4", _settlement.Id.Value, Guid.NewGuid(), 10m, "BRL", "p2p");

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<AccountNotFoundException>();
    }

    [Fact]
    public void Validator_rejects_transfer_to_the_same_account()
    {
        var id = Guid.NewGuid();
        var validator = new PostTransferValidator();

        var result = validator.Validate(new PostTransferCommand("ref-5", id, id, 10m, "BRL", "self"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(PostTransferCommand.ToAccountId));
    }
}
