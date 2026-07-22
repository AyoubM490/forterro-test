using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Forterro.BuildingBlocks.Api;

namespace Forterro.Payments.Worker.Infrastructure;

public enum BankPaymentStatus
{
    Received,
    PendingSca,
    InProgress,
    Settled,
    Rejected,
}

public sealed record PaymentOutcome(
    string PaymentId,
    BankPaymentStatus Status,
    string? BankReference,
    DateTimeOffset? SettledAt,
    string? RejectionCode,
    string? RejectionReason);

/// <summary>Erreur d'appel a l'API Open Banking, qualifiee comme rejouable ou non.</summary>
public sealed class OpenBankingCallException(string code, string message, bool isRetryable)
    : Exception(message)
{
    public string Code { get; } = code;

    public bool IsRetryable { get; } = isRetryable;
}

public interface IOpenBankingClient
{
    Task<PaymentOutcome> InitiateAsync(
        string debtorIban,
        string creditorIban,
        string creditorName,
        decimal amount,
        string currency,
        string endToEndId,
        string? remittanceInformation,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public sealed class OpenBankingClient(HttpClient httpClient, ILogger<OpenBankingClient> logger)
    : IOpenBankingClient
{
    public async Task<PaymentOutcome> InitiateAsync(
        string debtorIban,
        string creditorIban,
        string creditorName,
        decimal amount,
        string currency,
        string endToEndId,
        string? remittanceInformation,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/payments")
        {
            Content = JsonContent.Create(new
            {
                debtorIban,
                creditorIban,
                creditorName,
                amount,
                currency,
                endToEndId,
                remittanceInformation,
            }),
        };

        request.Headers.Add(IdempotencyFilter.HeaderName, idempotencyKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await BuildExceptionAsync(response, cancellationToken);
        }

        // Les options JSON doivent etre celles du contrat HTTP amont : l'API serialise
        // ses enums en chaines camelCase ("settled"), pas en entiers. Avec les options
        // par defaut, la deserialisation echoue sur le champ "status".
        var payload = await response.Content.ReadFromJsonAsync<PaymentApiResponse>(
                ApiJson.Options, cancellationToken)
            ?? throw new OpenBankingCallException("empty_response", "Reponse vide.", isRetryable: true);

        logger.LogInformation(
            "Paiement {PaymentId} initie, statut {Status}.", payload.PaymentId, payload.Status);

        return new PaymentOutcome(
            payload.PaymentId,
            payload.Status,
            payload.BankReference,
            payload.SettledAt,
            payload.RejectionCode,
            payload.RejectionReason);
    }

    /// <summary>
    /// L'API amont expose l'extension ProblemDetails <c>retryable</c>.
    /// On la lit plutot que de re-deduire le caractere rejouable du code HTTP :
    /// c'est le service qui parle a la banque qui detient l'information juste.
    /// </summary>
    private static async Task<OpenBankingCallException> BuildExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var code = "upstream_error";
        var retryable = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;
        var detail = response.ReasonPhrase ?? "Erreur inconnue";

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
                ApiJson.Options, cancellationToken);

            if (problem is not null)
            {
                code = problem.Code ?? code;
                retryable = problem.Retryable ?? retryable;
                detail = problem.Detail ?? detail;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException)
        {
            // Corps non conforme (page d'erreur d'un reverse proxy, par exemple) :
            // on garde la deduction basee sur le code HTTP.
        }

        return new OpenBankingCallException(code, detail, retryable);
    }

    private sealed record PaymentApiResponse(
        [property: JsonPropertyName("paymentId")] string PaymentId,
        [property: JsonPropertyName("status")] BankPaymentStatus Status,
        [property: JsonPropertyName("bankReference")] string? BankReference,
        [property: JsonPropertyName("settledAt")] DateTimeOffset? SettledAt,
        [property: JsonPropertyName("rejectionCode")] string? RejectionCode,
        [property: JsonPropertyName("rejectionReason")] string? RejectionReason);

    private sealed record ProblemPayload(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("retryable")] bool? Retryable,
        [property: JsonPropertyName("detail")] string? Detail);
}
