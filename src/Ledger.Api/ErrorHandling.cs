using FluentValidation;
using Ledger.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api;

/// <summary>
/// Translates exceptions into RFC 7807 problem details with the right status code.
/// The mapping is the contract: validation is 400, a broken business rule is 422,
/// a concurrency clash is 409 (safe to retry), everything else is 500 with no internals leaked.
/// </summary>
public sealed class LedgerExceptionHandler(ILogger<LedgerExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            ValidationException validation => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The request is invalid.",
                Extensions =
                {
                    ["errors"] = validation.Errors
                        .GroupBy(e => e.PropertyName)
                        .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()),
                },
            },
            AccountNotFoundException notFound => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Account not found.",
                Detail = notFound.Message,
            },
            LedgerException rule => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "The ledger rejected the operation.",
                Detail = rule.Message,
            },
            DbUpdateConcurrencyException => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The account changed while the request was being processed.",
                Detail = "Retry the request with the same Idempotency-Key.",
            },
            _ => null,
        };

        if (problem is null)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
            };
        }
        else
        {
            logger.LogInformation("Request rejected with {Status}: {Title} {Detail}", problem.Status, problem.Title, problem.Detail);
        }

        problem.Instance = httpContext.Request.Path;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
