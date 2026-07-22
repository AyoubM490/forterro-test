using System.Text.Json;
using System.Text.Json.Serialization;
using Forterro.Bff.Infrastructure;

namespace Forterro.Bff.Endpoints;

/// <summary>
/// La raison d'etre applicative du BFF : composer une reponse pour un ECRAN, pas pour un
/// service.
///
/// L'ecran "suivi de facture" a besoin de la facture ET de l'avancement du paiement. Ces deux
/// informations vivent dans deux services differents, et c'est correct : ce sont deux domaines
/// avec deux cycles de vie. Faire porter la jointure au client aurait trois consequences
/// concretes — deux allers-retours reseau depuis un navigateur mobile, une regle metier
/// ("emise depuis 10 minutes sans saga = anomalie") dupliquee dans chaque frontal, et un
/// couplage de l'ecran a la topologie interne des services.
/// </summary>
internal static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/invoices/{id:guid}/overview", GetOverviewAsync)
            .WithTags("Agregation")
            .WithSummary("Facture et avancement du paiement en un seul appel.")
            .Produces<InvoiceOverview>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetOverviewAsync(
        Guid id,
        InvoicingClient invoicing,
        PaymentsClient payments,
        CancellationToken cancellationToken)
    {
        // Les deux appels partent EN MEME TEMPS. Les enchainer doublerait la latence percue
        // sans rien apporter : ils sont independants.
        var invoiceTask = invoicing.GetInvoiceAsync(id, cancellationToken);
        var sagaTask = payments.GetSagaByInvoiceAsync(id, cancellationToken);

        await Task.WhenAll(invoiceTask, sagaTask);

        var invoice = await invoiceTask;

        // La facture est la ressource principale : sans elle il n'y a rien a afficher.
        if (invoice.Outcome is not DownstreamOutcome.Ok)
        {
            return invoice.Outcome switch
            {
                DownstreamOutcome.NotFound => Results.Problem(
                    title: "Facture introuvable",
                    statusCode: StatusCodes.Status404NotFound),
                DownstreamOutcome.Forbidden => Results.Problem(
                    title: "Acces refuse",
                    detail: "Le scope invoicing:read est requis.",
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Problem(
                    title: "Service de facturation indisponible",
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        }

        var saga = await sagaTask;

        // Le paiement, lui, est secondaire : son indisponibilite degrade la reponse, elle ne la
        // supprime pas. Renvoyer 503 pour toute la page parce qu'un bloc secondaire est en
        // panne, c'est propager la panne au lieu de la contenir.
        return Results.Ok(new InvoiceOverview
        {
            Invoice = invoice.Value,
            Payment = saga.Outcome is DownstreamOutcome.Ok ? saga.Value : null,
            PaymentAvailability = Describe(saga.Outcome),
            Status = Summarize(invoice.Value, saga),
        });
    }

    private static PaymentAvailability Describe(DownstreamOutcome outcome) => outcome switch
    {
        DownstreamOutcome.Ok => PaymentAvailability.Available,
        DownstreamOutcome.NotFound => PaymentAvailability.NotStarted,
        DownstreamOutcome.Forbidden => PaymentAvailability.Forbidden,
        _ => PaymentAvailability.Unavailable,
    };

    /// <summary>
    /// Traduit deux etats techniques en une seule phrase affichable.
    ///
    /// C'est typiquement ce qui n'a pas sa place dans un service metier — la facturation n'a
    /// pas a connaitre les etats de saga — ni dans le frontal, ou la regle serait reecrite
    /// pour chaque ecran et divergerait.
    /// </summary>
    private static string Summarize(JsonElement invoice, DownstreamResult<JsonElement> saga)
    {
        var invoiceStatus = invoice.TryGetProperty("status", out var status)
            ? status.GetString()
            : null;

        if (invoiceStatus is "paid")
        {
            return "Facture reglee.";
        }

        if (saga.Outcome is DownstreamOutcome.Forbidden)
        {
            return "Avancement du paiement non visible avec les droits actuels.";
        }

        if (saga.Outcome is DownstreamOutcome.Unavailable)
        {
            return "Avancement du paiement temporairement indisponible.";
        }

        if (saga.Outcome is DownstreamOutcome.NotFound)
        {
            return invoiceStatus is "issued"
                ? "Facture emise, paiement pas encore pris en charge."
                : "Aucun paiement en cours.";
        }

        var sagaState = saga.Value.TryGetProperty("state", out var state) ? state.GetString() : null;

        return sagaState switch
        {
            "started" => "Paiement pris en charge.",
            "awaitingBank" => "Ordre transmis a la banque, en attente de confirmation.",
            "awaitingRetry" => FormatRetry(saga.Value),
            "settled" => "Virement execute.",
            "failed" => FormatFailure(saga.Value),
            "aborted" => "Paiement annule avant execution.",
            _ => "Etat du paiement inconnu.",
        };
    }

    private static string FormatRetry(JsonElement saga)
    {
        var reason = saga.TryGetProperty("failureReason", out var value) ? value.GetString() : null;

        return reason is null
            ? "Nouvelle tentative planifiee."
            : $"Nouvelle tentative planifiee ({reason}).";
    }

    private static string FormatFailure(JsonElement saga)
    {
        var code = saga.TryGetProperty("failureCode", out var value) ? value.GetString() : null;

        // Le seul cas ou l'ecran doit appeler un humain plutot qu'inviter a reessayer.
        return code is "compensation_required"
            ? "Virement parti puis facture annulee : intervention manuelle requise."
            : "Paiement en echec definitif.";
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<PaymentAvailability>))]
public enum PaymentAvailability
{
    Available,
    NotStarted,
    Forbidden,
    Unavailable,
}

public sealed record InvoiceOverview
{
    /// <summary>
    /// Charge utile de la facture transmise telle quelle. Le BFF ne redefinit pas le contrat
    /// de facturation : le recopier dans un DTO local garantirait qu'il diverge au premier
    /// champ ajoute en amont, sans qu'aucun test ne le signale.
    /// </summary>
    public required JsonElement Invoice { get; init; }

    public JsonElement? Payment { get; init; }

    public required PaymentAvailability PaymentAvailability { get; init; }

    /// <summary>Phrase prete a afficher, deduite des deux etats.</summary>
    public required string Status { get; init; }
}
