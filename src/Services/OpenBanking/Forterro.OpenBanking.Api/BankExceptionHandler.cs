using System.Diagnostics;
using Forterro.OpenBanking.Api.Bank;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Forterro.OpenBanking.Api;

/// <summary>
/// Traduit une <see cref="BankException"/> en reponse HTTP.
///
/// L'indication <c>retryable</c> est propagee au client : c'est elle qui permet a la saga
/// de distinguer "reessaie dans 30 secondes" de "compense, ce sera toujours non".
/// Enregistre AVANT le handler generique : le premier qui traite gagne.
/// </summary>
public sealed class BankExceptionHandler(ILogger<BankExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not BankException bankException)
        {
            return false;
        }

        var status = bankException.Code switch
        {
            "invalid_request" => StatusCodes.Status400BadRequest,
            "tpp_not_authorized" => StatusCodes.Status403Forbidden,
            "not_found" => StatusCodes.Status404NotFound,
            "conflict" => StatusCodes.Status409Conflict,
            "rate_limited" => StatusCodes.Status429TooManyRequests,
            "bank_unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status502BadGateway,
        };

        logger.LogWarning(
            "Erreur banque {Code} (rejouable : {Retryable}) : {Message}",
            bankException.Code, bankException.IsRetryable, bankException.Message);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = "Erreur du partenaire bancaire",
            Type = $"https://docs.forterro.com/errors/bank/{bankException.Code}",
            Detail = bankException.Message,
        };

        problem.Extensions["code"] = bankException.Code;
        problem.Extensions["retryable"] = bankException.IsRetryable;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = status;

        // Retry-After : le client sait quand revenir, il n'a pas a deviner.
        if (bankException.IsRetryable)
        {
            httpContext.Response.Headers.RetryAfter = "30";
        }

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
