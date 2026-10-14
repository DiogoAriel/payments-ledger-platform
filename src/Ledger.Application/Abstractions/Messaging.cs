using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Application.Abstractions;

/// <summary>A command changes state and returns a result. One handler per command.</summary>
public interface ICommand<TResult>;

/// <summary>A query reads state and returns a result. It must not change anything.</summary>
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Entry point used by the API. It resolves the handler, runs the FluentValidation validators
/// registered for the message, and only then calls the handler.
/// A small hand-written dispatcher keeps the pipeline explicit and dependency-free.
/// </summary>
public interface ISender
{
    Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
    Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}

internal sealed class Sender(IServiceProvider services) : ISender
{
    public async Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(command, cancellationToken);

        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult));
        dynamic handler = services.GetRequiredService(handlerType);
        return await handler.Handle((dynamic)command, cancellationToken);
    }

    public async Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(query, cancellationToken);

        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
        dynamic handler = services.GetRequiredService(handlerType);
        return await handler.Handle((dynamic)query, cancellationToken);
    }

    private async Task ValidateAsync<TMessage>(TMessage message, CancellationToken cancellationToken) where TMessage : notnull
    {
        var validatorType = typeof(IValidator<>).MakeGenericType(message.GetType());
        var failures = new List<FluentValidation.Results.ValidationFailure>();

        foreach (var validator in services.GetServices(validatorType).OfType<IValidator>())
        {
            var context = new ValidationContext<object>(message);
            var result = await validator.ValidateAsync(context, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }
}
