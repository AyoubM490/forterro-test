using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forterro.BuildingBlocks.Api;

/// <summary>Erreur metier attendue : se traduit en 4xx, pas en 500.</summary>
public class BusinessRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Ressource absente.</summary>
public sealed class ResourceNotFoundException(string resource, object key)
    : BusinessRuleException("resource_not_found", $"{resource} '{key}' introuvable.");

/// <summary>Transition d'etat interdite (ex : payer une facture annulee).</summary>
public sealed class InvalidStateTransitionException(string message)
    : BusinessRuleException("invalid_state_transition", message);

/// <summary>
/// Handler global : toute exception sort en RFC 7807 (ProblemDetails).
/// On n'expose jamais la stack trace hors developpement, mais on renvoie systematiquement
/// le traceId : le client peut le donner au support, qui retrouve la trace exacte.
/// </summary>
public sealed class ForterroExceptionHandler(
    IHostEnvironment environment,
    ILogger<ForterroExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>499, convention nginx : le client a coupe avant la reponse.</summary>
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var (status, title, code) = exception switch
        {
            ResourceNotFoundException e => (StatusCodes.Status404NotFound, "Ressource introuvable", e.Code),
            InvalidStateTransitionException e => (StatusCodes.Status409Conflict, "Transition invalide", e.Code),
            BusinessRuleException e => (StatusCodes.Status422UnprocessableEntity, "Regle metier violee", e.Code),
            ArgumentException => (StatusCodes.Status400BadRequest, "Requete invalide", "invalid_argument"),
            OperationCanceledException => (ClientClosedRequest, "Requete annulee", "cancelled"),
            TimeoutException => (StatusCodes.Status504GatewayTimeout, "Delai depasse", "timeout"),
            _ => (StatusCodes.Status500InternalServerError, "Erreur interne", "internal_error"),
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Erreur non geree : {Message}", exception.Message);
        }
        else
        {
            logger.LogWarning("Erreur metier {Code} : {Message}", code, exception.Message);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://docs.forterro.com/errors/{code}",
            Detail = status >= StatusCodes.Status500InternalServerError && !environment.IsDevelopment()
                ? "Une erreur interne est survenue. Contactez le support avec le traceId."
                : exception.Message,
            Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}",
        };

        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        if (httpContext.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var correlationId))
        {
            problem.Extensions["correlationId"] = correlationId;
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);

        return true;
    }
}
