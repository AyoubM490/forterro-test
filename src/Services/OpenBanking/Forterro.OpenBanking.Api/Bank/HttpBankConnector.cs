using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Forterro.BuildingBlocks.Banking;
using Forterro.BuildingBlocks.Observability;

namespace Forterro.OpenBanking.Api.Bank;

public sealed class BankApiOptions
{
    public const string SectionName = "BankApi";

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Identifiant du TPP (Third Party Provider) enregistre aupres de la banque.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Passe en mode simule (aucun appel reseau) pour la demo et les tests.</summary>
    public bool UseSimulator { get; set; }
}

/// <summary>
/// Implementation HTTP d'une API PSD2 de type Berlin Group.
/// La resilience (retry, circuit breaker, timeouts) n'est PAS ici : elle est declaree
/// sur le HttpClient dans Program.cs, ce qui garde ce connecteur lisible et testable.
/// </summary>
public sealed class HttpBankConnector(HttpClient httpClient, ILogger<HttpBankConnector> logger) : IBankConnector
{
    public async Task<PaymentResult> InitiatePaymentAsync(
        PaymentInitiation initiation,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initiation);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        using var activity = Telemetry.ActivitySource.StartActivity("bank.initiate_payment", ActivityKind.Client);
        activity?.SetTag("forterro.end_to_end_id", initiation.EndToEndId);
        // On trace l'IBAN masque : une trace part chez un fournisseur d'APM tiers.
        activity?.SetTag("forterro.debtor_iban", Iban.Mask(initiation.DebtorIban));

        var stopwatch = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/payments/sepa-credit-transfers")
        {
            Content = JsonContent.Create(new BankPaymentRequest(
                new BankAccountRef(initiation.DebtorIban),
                new BankAccountRef(initiation.CreditorIban),
                initiation.CreditorName,
                new BankAmount(initiation.Currency, initiation.Amount),
                initiation.EndToEndId,
                initiation.RemittanceInformation)),
        };

        // X-Request-ID est la cle d'idempotence de la norme Berlin Group.
        request.Headers.Add("X-Request-ID", idempotencyKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        stopwatch.Stop();
        Telemetry.ExternalCallDuration.Record(
            stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", "initiate_payment"),
            new KeyValuePair<string, object?>("status_code", (int)response.StatusCode));

        await EnsureSuccessAsync(response, cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<BankPaymentResponse>(cancellationToken)
            ?? throw new BankException("empty_response", "Reponse vide de la banque.", isRetryable: true);

        logger.LogInformation(
            "Virement {EndToEndId} initie, statut banque {Status}.",
            initiation.EndToEndId, payload.TransactionStatus);

        return Map(payload);
    }

    public async Task<PaymentResult> GetPaymentStatusAsync(
        string bankPaymentId,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            new Uri($"v1/payments/sepa-credit-transfers/{bankPaymentId}/status", UriKind.Relative),
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<BankPaymentResponse>(cancellationToken)
            ?? throw new BankException("empty_response", "Reponse vide de la banque.", isRetryable: true);

        return Map(payload);
    }

    public async Task<AccountBalance> GetBalanceAsync(string iban, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            new Uri($"v1/accounts/{Iban.Normalize(iban)}/balances", UriKind.Relative),
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<BankBalanceResponse>(cancellationToken)
            ?? throw new BankException("empty_response", "Reponse vide de la banque.", isRetryable: true);

        return new AccountBalance(
            iban,
            payload.Available.Amount,
            payload.Booked.Amount,
            payload.Available.Currency,
            payload.AsOf);
    }

    /// <summary>
    /// Traduit le code HTTP en exception metier en qualifiant le caractere rejouable.
    /// C'est cette information que la saga utilise pour decider entre nouvelle tentative
    /// et compensation definitive.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        throw response.StatusCode switch
        {
            HttpStatusCode.BadRequest => new BankException("invalid_request", body, isRetryable: false),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => new BankException("tpp_not_authorized", body, isRetryable: false),
            HttpStatusCode.NotFound => new BankException("not_found", body, isRetryable: false),
            HttpStatusCode.Conflict => new BankException("conflict", body, isRetryable: false),
            HttpStatusCode.TooManyRequests => new BankException("rate_limited", body, isRetryable: true),
            _ when (int)response.StatusCode >= 500
                => new BankException("bank_unavailable", body, isRetryable: true),
            _ => new BankException("unexpected", body, isRetryable: false),
        };
    }

    private static PaymentResult Map(BankPaymentResponse payload)
    {
        var status = payload.TransactionStatus switch
        {
            "RCVD" => PaymentStatus.Received,
            "ACTC" or "ACCP" => PaymentStatus.InProgress,
            "ACSP" => PaymentStatus.InProgress,
            "ACSC" => PaymentStatus.Settled,
            "RJCT" => PaymentStatus.Rejected,
            "PDNG" => PaymentStatus.PendingSca,
            _ => PaymentStatus.Received,
        };

        return new PaymentResult(
            payload.PaymentId,
            status,
            payload.EndToEndId,
            status == PaymentStatus.Settled ? payload.LastUpdated : null,
            payload.RejectionCode,
            payload.RejectionReason);
    }

    // --- DTO du dialecte Berlin Group ------------------------------------
    private sealed record BankAccountRef([property: JsonPropertyName("iban")] string Iban);

    private sealed record BankAmount(
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("amount")] decimal Amount);

    private sealed record BankPaymentRequest(
        [property: JsonPropertyName("debtorAccount")] BankAccountRef DebtorAccount,
        [property: JsonPropertyName("creditorAccount")] BankAccountRef CreditorAccount,
        [property: JsonPropertyName("creditorName")] string CreditorName,
        [property: JsonPropertyName("instructedAmount")] BankAmount InstructedAmount,
        [property: JsonPropertyName("endToEndIdentification")] string EndToEndId,
        [property: JsonPropertyName("remittanceInformationUnstructured")] string? RemittanceInformation);

    private sealed record BankPaymentResponse(
        [property: JsonPropertyName("paymentId")] string PaymentId,
        [property: JsonPropertyName("transactionStatus")] string TransactionStatus,
        [property: JsonPropertyName("endToEndIdentification")] string? EndToEndId,
        [property: JsonPropertyName("lastUpdated")] DateTimeOffset? LastUpdated,
        [property: JsonPropertyName("rejectionCode")] string? RejectionCode,
        [property: JsonPropertyName("rejectionReason")] string? RejectionReason);

    private sealed record BankBalanceResponse(
        [property: JsonPropertyName("available")] BankAmount Available,
        [property: JsonPropertyName("booked")] BankAmount Booked,
        [property: JsonPropertyName("asOf")] DateTimeOffset AsOf);
}
