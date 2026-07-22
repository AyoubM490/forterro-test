using Forterro.Payments.Worker.Domain;
using Forterro.Payments.Worker.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Forterro.Payments.Worker.Endpoints;

/// <summary>
/// Lecture seule de l'etat des sagas.
///
/// Un worker n'a pas vocation a exposer une API, et celle-ci reste volontairement minimale :
/// aucune commande, aucun moyen de forcer une transition. Elle existe parce que la question
/// "ou en est le paiement de cette facture ?" n'a de reponse nulle part ailleurs — la facture
/// sait seulement qu'elle est <c>issued</c>, pas que la banque a repondu AM04 et que la
/// prochaine tentative est dans 4 minutes.
/// </summary>
internal static class SagaEndpoints
{
    public static IEndpointRouteBuilder MapSagaEndpoints(this IEndpointRouteBuilder app)
    {
        // "payment-sagas" et non "payments" : l'API Open Banking expose deja /api/v1/payments
        // pour les ordres bancaires. Deux ressources differentes derriere le meme chemin, ce
        // serait une collision de routage impossible a demeler a la porte d'entree.
        var group = app.MapGroup("/api/v1/payment-sagas")
            .WithTags("Sagas de paiement")
            .RequireAuthorization(PaymentPolicies.PaymentsRead);

        group.MapGet("/by-invoice/{invoiceId:guid}", GetByInvoiceAsync)
            .WithSummary("Etat de la saga de paiement d'une facture.")
            .Produces<SagaResponse>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetByInvoiceAsync(
        Guid invoiceId,
        PaymentsDbContext dbContext,
        CancellationToken cancellationToken)
    {
        // AsNoTracking : lecture pure, aucun interet a peupler le change tracker.
        var saga = await dbContext.Sagas
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.InvoiceId == invoiceId, cancellationToken);

        return saga is null
            ? Results.Problem(
                title: "Aucune saga pour cette facture",
                detail: "La facture n'a pas encore ete emise, ou l'evenement n'est pas encore consomme.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(SagaResponse.From(saga));
    }
}

public static class PaymentPolicies
{
    public const string PaymentsRead = "payments:read";
}

/// <summary>Projection de lecture. Le modele de domaine n'est jamais serialise tel quel.</summary>
public sealed record SagaResponse
{
    public required Guid Id { get; init; }

    public required Guid InvoiceId { get; init; }

    public required string InvoiceNumber { get; init; }

    public required SagaState State { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>IBAN tronque. Un identifiant de compte complet n'a rien a faire dans une reponse de suivi.</summary>
    public required string DebtorIbanMasked { get; init; }

    public required int Attempts { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    public string? BankReference { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureReason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public static SagaResponse From(PaymentSaga saga)
    {
        ArgumentNullException.ThrowIfNull(saga);

        return new SagaResponse
        {
            Id = saga.Id,
            InvoiceId = saga.InvoiceId,
            InvoiceNumber = saga.InvoiceNumber,
            State = saga.State,
            Amount = saga.Amount,
            Currency = saga.Currency,
            DebtorIbanMasked = Mask(saga.DebtorIban),
            Attempts = saga.Attempts,
            NextAttemptAt = saga.NextAttemptAt,
            BankReference = saga.BankReference,
            FailureCode = saga.FailureCode,
            FailureReason = saga.FailureReason,
            CreatedAt = saga.CreatedAt,
            CompletedAt = saga.CompletedAt,
        };
    }

    private static string Mask(string iban)
        => iban.Length <= 4 ? new string('*', iban.Length) : $"{new string('*', iban.Length - 4)}{iban[^4..]}";
}
