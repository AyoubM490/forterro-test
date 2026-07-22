using Forterro.Intelligence.Api.Extraction;
using Forterro.Intelligence.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Forterro.Intelligence.Api.Endpoints;

public static class Policies
{
    /// <summary>
    /// Scope dedie, conforme a la regle de l'ADR 0005 : un scope par couple
    /// ressource/verbe, jamais par cas d'usage. Extraire un document n'est pas
    /// ecrire une facture — une ligne produit peut avoir le droit de soumettre
    /// des documents sans pouvoir emettre quoi que ce soit.
    /// </summary>
    public const string DocumentsExtract = "documents:extract";
}

public static class ExtractionEndpoints
{
    public static void MapExtractionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/extractions")
            .WithTags("Extraction")
            .RequireAuthorization(Policies.DocumentsExtract);

        group.MapPost("/invoices", ExtractInvoiceAsync)
            .WithName("ExtractInvoice")
            .WithSummary("Extrait les champs d'une facture fournisseur (brouillon).")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ExtractionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();
    }

    private static async Task<IResult> ExtractInvoiceAsync(
        [FromForm] IFormFile document,
        InvoiceExtractionService service,
        IOptions<ExtractionOptions> options,
        CancellationToken cancellationToken)
    {
        if (document.Length == 0)
        {
            return TypedResults.Problem("Document vide.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Garde-fou AVANT l'inference : on refuse un document hors gabarit plutot que
        // de payer une inference pour decouvrir qu'il etait trop gros. Un cout variable
        // au volume n'existe nulle part ailleurs dans ce systeme ; il se plafonne en
        // amont, pas apres coup.
        if (document.Length > options.Value.MaxDocumentBytes)
        {
            return TypedResults.Problem(
                $"Document trop volumineux ({document.Length} octets).",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken);

        var outcome = await service.ExtractAsync(buffer.ToArray(), cancellationToken);

        // Une extraction rejetee n'est PAS une erreur serveur : le service a
        // parfaitement fonctionne, c'est la proposition du modele qui est mauvaise.
        // 422 le dit ; un 500 ferait croire a une panne et declencherait des reprises
        // inutiles sur un document qui echouera toujours.
        if (outcome.Status == ExtractionStatus.Rejected)
        {
            return TypedResults.Problem(
                title: "Extraction refusee par la validation metier",
                detail: string.Join(", ", outcome.Violations),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return TypedResults.Ok(ExtractionResponse.From(outcome));
    }
}

/// <summary>
/// Reponse exposee. Le total transmis est celui RECALCULE a partir des lignes,
/// jamais celui annonce par le modele : un appelant ne doit pas pouvoir consommer
/// une valeur que le domaine n'a pas verifiee.
/// </summary>
public sealed record ExtractionResponse(
    string DocumentHash,
    string Status,
    string Model,
    double Confidence,
    decimal? TotalInclTax,
    ModelExtraction? Fields)
{
    public static ExtractionResponse From(ExtractionOutcome outcome) => new(
        outcome.DocumentHash,
        outcome.Status switch
        {
            ExtractionStatus.Draft => "draft",
            ExtractionStatus.ReviewRequired => "reviewRequired",
            _ => "rejected",
        },
        outcome.Model,
        outcome.Confidence,
        outcome.ComputedTotalInclTax,
        outcome.Extraction);
}
