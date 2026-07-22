using FluentValidation;
using Forterro.BuildingBlocks.Api;
using Forterro.BuildingBlocks.Banking;
using Forterro.OpenBanking.Api.Bank;

namespace Forterro.OpenBanking.Api.Endpoints;

public sealed record InitiatePaymentRequest(
    string DebtorIban,
    string CreditorIban,
    string CreditorName,
    decimal Amount,
    string Currency,
    string EndToEndId,
    string? RemittanceInformation);

public sealed record PaymentResponse(
    string PaymentId,
    PaymentStatus Status,
    string? BankReference,
    DateTimeOffset? SettledAt,
    string? RejectionCode,
    string? RejectionReason);

public sealed record BalanceResponse(
    string Iban,
    decimal Available,
    decimal Booked,
    string Currency,
    DateTimeOffset AsOf);

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapOpenBankingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var payments = app.MapGroup("/api/v1/payments")
            .WithTags("Payments")
            .RequireAuthorization(Policies.PaymentsWrite);

        payments.MapPost("/", InitiateAsync)
            .WithName("InitiatePayment")
            .WithSummary("Initie un virement SEPA (PSD2 Payment Initiation Service).")
            .WithDescription("L'en-tete Idempotency-Key est OBLIGATOIRE : c'est ce qui empeche un double debit en cas de retry.")
            .Produces<PaymentResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();

        payments.MapGet("/{bankPaymentId}", GetStatusAsync)
            .WithName("GetPaymentStatus")
            .WithSummary("Consulte le statut d'un virement.")
            .RequireAuthorization(Policies.PaymentsRead)
            .Produces<PaymentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/api/v1/accounts/{iban}/balance", GetBalanceAsync)
            .WithTags("Accounts")
            .WithName("GetAccountBalance")
            .WithSummary("Solde d'un compte (PSD2 Account Information Service).")
            .RequireAuthorization(Policies.AccountsRead)
            .Produces<BalanceResponse>();

        return app;
    }

    private static async Task<IResult> InitiateAsync(
        InitiatePaymentRequest request,
        IValidator<InitiatePaymentRequest> validator,
        IBankConnector bank,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(validation.ToDictionary());
        }

        // Contrairement a la facturation, la cle est ici exigee, pas optionnelle :
        // sans elle on ne peut pas garantir l'absence de double debit sur un retry.
        var idempotencyKey = http.Request.Headers[IdempotencyFilter.HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new BusinessRuleException(
                "idempotency_key_required",
                $"L'en-tete {IdempotencyFilter.HeaderName} est obligatoire sur l'initiation de paiement.");
        }

        var result = await bank.InitiatePaymentAsync(
            new PaymentInitiation
            {
                DebtorIban = request.DebtorIban,
                CreditorIban = request.CreditorIban,
                CreditorName = request.CreditorName,
                Amount = request.Amount,
                Currency = request.Currency,
                EndToEndId = request.EndToEndId,
                RemittanceInformation = request.RemittanceInformation,
            },
            idempotencyKey,
            cancellationToken);

        var response = new PaymentResponse(
            result.BankPaymentId,
            result.Status,
            result.BankReference,
            result.SettledAt,
            result.RejectionCode,
            result.RejectionReason);

        // 202 et non 201 : un virement SEPA n'est pas execute de maniere synchrone.
        // Le client interroge ensuite le statut, ou attend l'evenement.
        return Results.Accepted($"/api/v1/payments/{result.BankPaymentId}", response);
    }

    private static async Task<IResult> GetStatusAsync(
        string bankPaymentId,
        IBankConnector bank,
        CancellationToken cancellationToken)
    {
        var result = await bank.GetPaymentStatusAsync(bankPaymentId, cancellationToken);

        return Results.Ok(new PaymentResponse(
            result.BankPaymentId,
            result.Status,
            result.BankReference,
            result.SettledAt,
            result.RejectionCode,
            result.RejectionReason));
    }

    private static async Task<IResult> GetBalanceAsync(
        string iban,
        IBankConnector bank,
        CancellationToken cancellationToken)
    {
        if (!Iban.IsValid(iban))
        {
            throw new BusinessRuleException("invalid_iban", $"IBAN invalide : {Iban.Mask(iban)}.");
        }

        var balance = await bank.GetBalanceAsync(iban, cancellationToken);

        return Results.Ok(new BalanceResponse(
            Iban.Mask(balance.Iban),
            balance.Available,
            balance.Booked,
            balance.Currency,
            balance.AsOf));
    }
}

public static class Policies
{
    public const string PaymentsRead = "payments:read";
    public const string PaymentsWrite = "payments:write";
    public const string AccountsRead = "accounts:read";
}
