using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Forterro.BuildingBlocks.Api;

/// <summary>Reponse memorisee pour une cle d'idempotence.</summary>
public sealed record IdempotentResponse(int StatusCode, string Body, string RequestFingerprint);

public interface IIdempotencyStore
{
    Task<IdempotentResponse?> GetAsync(string key, CancellationToken cancellationToken);

    Task SaveAsync(string key, IdempotentResponse response, CancellationToken cancellationToken);
}

/// <summary>
/// Filtre d'idempotence sur les POST non rejouables (creation de facture, ordre de paiement).
///
/// Cas reel : le client envoie POST /payments, le reseau coupe avant la reponse, il rejoue.
/// Sans cle d'idempotence, le debit part deux fois. Avec, le second appel recoit
/// la reponse du premier, a l'identique.
///
/// L'empreinte du corps est verifiee : reutiliser une cle avec un corps different
/// est une erreur du client (422), pas un cas a servir depuis le cache.
/// </summary>
public sealed class IdempotencyFilter(
    IIdempotencyStore store,
    ILogger<IdempotencyFilter> logger) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(key))
        {
            // Volontairement optionnel : on n'impose pas la cle, on la respecte si elle est fournie.
            return await next(context);
        }

        // La cle est portee par la route : la meme cle sur deux endpoints differents
        // designe deux operations differentes, pas un rejeu.
        var storageKey = BuildStorageKey(key, http.Request.Path);

        var fingerprint = await ComputeFingerprintAsync(http);
        var cached = await store.GetAsync(storageKey, http.RequestAborted);

        if (cached is not null)
        {
            if (!string.Equals(cached.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new BusinessRuleException(
                    "idempotency_key_reuse",
                    $"La cle d'idempotence '{key}' a deja ete utilisee avec un corps de requete different.");
            }

            logger.LogInformation("Rejeu idempotent servi pour la cle {Key}.", key);
            http.Response.Headers["Idempotency-Replayed"] = "true";

            return Results.Content(cached.Body, "application/json", Encoding.UTF8, cached.StatusCode);
        }

        var result = await next(context);

        var (statusCode, body) = await SerializeResultAsync(result, http);

        // On ne memorise que les succes : une erreur 5xx doit pouvoir etre rejouee.
        if (statusCode is >= 200 and < 300)
        {
            await store.SaveAsync(
                storageKey, new IdempotentResponse(statusCode, body, fingerprint), http.RequestAborted);
        }

        return result;
    }

    /// <summary>
    /// Empreinte du corps de la requete. Suppose que
    /// <see cref="RequestBufferingMiddleware"/> a rendu le flux rembobinable :
    /// sans lui, on hacherait un flux deja consomme, donc vide.
    /// </summary>
    private static async Task<string> ComputeFingerprintAsync(HttpContext http)
    {
        var body = http.Request.Body;

        if (!body.CanSeek)
        {
            throw new InvalidOperationException(
                "Le corps de la requete n'est pas rembobinable. Ajoutez app.UseRequestBuffering() "
                + "avant le routage pour utiliser IdempotencyFilter.");
        }

        var originalPosition = body.Position;
        body.Position = 0;

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(body, http.RequestAborted);

        body.Position = originalPosition;
        return Convert.ToHexString(hash);
    }

    private static async Task<(int StatusCode, string Body)> SerializeResultAsync(object? result, HttpContext http)
    {
        if (result is IStatusCodeHttpResult { StatusCode: not null } statusResult)
        {
            var value = (result as IValueHttpResult)?.Value;
            return (statusResult.StatusCode.Value, JsonSerializer.Serialize(value, ApiJson.Options));
        }

        if (result is IValueHttpResult valueResult)
        {
            return (StatusCodes.Status200OK, JsonSerializer.Serialize(valueResult.Value, ApiJson.Options));
        }

        await Task.CompletedTask;
        return (http.Response.StatusCode, string.Empty);
    }

    internal static string BuildStorageKey(string key, string route)
        => string.Create(CultureInfo.InvariantCulture, $"{route}|{key}");
}
