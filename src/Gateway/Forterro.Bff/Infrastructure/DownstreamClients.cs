using System.Net;
using System.Text.Json;

namespace Forterro.Bff.Infrastructure;

/// <summary>
/// Resultat d'un appel en aval, avec la distinction dont l'agregation a besoin :
/// "pas trouve", "pas le droit" et "en panne" ne se presentent pas de la meme facon a l'ecran.
/// </summary>
internal enum DownstreamOutcome
{
    Ok,
    NotFound,
    Forbidden,
    Unavailable,
}

internal sealed record DownstreamResult<T>(DownstreamOutcome Outcome, T? Value)
{
    public static DownstreamResult<T> Ok(T value) => new(DownstreamOutcome.Ok, value);

    public static DownstreamResult<T> Failure(DownstreamOutcome outcome) => new(outcome, default);
}

/// <summary>
/// Base des clients en aval : traduit les issues HTTP en <see cref="DownstreamOutcome"/>.
///
/// Aucune exception ne remonte de ces appels. Une agregation doit pouvoir livrer une reponse
/// partielle ; si un client en aval levait, le premier service en panne emporterait la reponse
/// entiere — et le BFF deviendrait le point qui propage les pannes au lieu de les contenir.
/// </summary>
internal abstract class DownstreamClientBase(HttpClient httpClient, ILogger logger)
{
    protected async Task<DownstreamResult<JsonElement>> GetJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return DownstreamResult<JsonElement>.Failure(DownstreamOutcome.NotFound);

                // 403 n'est pas une panne : c'est un appelant dont le jeton ne porte pas le
                // scope. L'agregation doit pouvoir le dire a l'ecran plutot que d'echouer.
                case HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized:
                    return DownstreamResult<JsonElement>.Failure(DownstreamOutcome.Forbidden);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Reponse {Status} de {Path}.", (int)response.StatusCode, path);
                return DownstreamResult<JsonElement>.Failure(DownstreamOutcome.Unavailable);
            }

            return DownstreamResult<JsonElement>.Ok(
                await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Appel en aval {Path} en echec.", path);
            return DownstreamResult<JsonElement>.Failure(DownstreamOutcome.Unavailable);
        }
    }
}

/// <summary>Client de l'API de facturation.</summary>
internal sealed class InvoicingClient(HttpClient httpClient, ILogger<InvoicingClient> logger)
    : DownstreamClientBase(httpClient, logger)
{
    public Task<DownstreamResult<JsonElement>> GetInvoiceAsync(Guid id, CancellationToken cancellationToken)
        => GetJsonAsync($"api/v1/invoices/{id}", cancellationToken);
}

/// <summary>Client de lecture des sagas de paiement.</summary>
internal sealed class PaymentsClient(HttpClient httpClient, ILogger<PaymentsClient> logger)
    : DownstreamClientBase(httpClient, logger)
{
    public Task<DownstreamResult<JsonElement>> GetSagaByInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken)
        => GetJsonAsync($"api/v1/payment-sagas/by-invoice/{invoiceId}", cancellationToken);
}
