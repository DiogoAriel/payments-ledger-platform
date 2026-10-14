using FluentValidation;
using Ledger.Application.Abstractions;
using Ledger.Application.Webhooks;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers every command/query handler and validator found in this assembly.
    /// Scanning by interface keeps registration from drifting out of sync with the code.
    /// </summary>
    public static IServiceCollection AddLedgerApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var handlerInterface in type.GetInterfaces().Where(IsHandlerInterface))
            {
                services.AddScoped(handlerInterface, type);
            }
        }

        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
        services.AddScoped<ISender, Sender>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<WebhookVerifier>();

        return services;
    }

    private static bool IsHandlerInterface(Type type) =>
        type.IsGenericType &&
        (type.GetGenericTypeDefinition() == typeof(ICommandHandler<,>) ||
         type.GetGenericTypeDefinition() == typeof(IQueryHandler<,>));
}
