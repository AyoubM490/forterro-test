using Forterro.Intelligence.Api.Models;

namespace Forterro.Intelligence.Api.Extraction;

/// <summary>Ce que le service a decide de la proposition du modele.</summary>
public enum ExtractionStatus
{
    /// <summary>Valide et sure : un BROUILLON de facture, jamais une facture emise.</summary>
    Draft,

    /// <summary>Valide mais peu sure : file de revue humaine.</summary>
    ReviewRequired,

    /// <summary>
    /// Rejetee par le domaine. Le modele s'est trompe, quelle que soit sa confiance.
    /// Aucune donnee extraite n'est exposee : elle a echoue aux invariants, la
    /// transmettre inviterait un appelant a l'utiliser quand meme.
    /// </summary>
    Rejected,
}

/// <summary>
/// Resultat d'une extraction. Immuable, et construit uniquement par les fabriques
/// ci-dessous : il est impossible de fabriquer un Draft sans validation reussie.
/// </summary>
public sealed record ExtractionOutcome
{
    private ExtractionOutcome() { }

    public required ExtractionStatus Status { get; init; }

    /// <summary>Empreinte SHA-256 du document : cle d'idempotence et de tracabilite.</summary>
    public required string DocumentHash { get; init; }

    /// <summary>Modele ayant produit la proposition. Indispensable pour rejouer un incident.</summary>
    public required string Model { get; init; }

    public required double Confidence { get; init; }

    /// <summary>Champs extraits. NUL quand l'extraction est rejetee.</summary>
    public ModelExtraction? Extraction { get; init; }

    /// <summary>Total recalcule a partir des lignes, jamais celui annonce par le modele.</summary>
    public decimal? ComputedTotalInclTax { get; init; }

    /// <summary>Codes de violation, non vides uniquement en cas de rejet.</summary>
    public IReadOnlyList<string> Violations { get; init; } = [];

    public static ExtractionOutcome Draft(
        string documentHash, string model, ModelExtraction extraction, ExtractionValidation validation)
        => new()
        {
            Status = ExtractionStatus.Draft,
            DocumentHash = documentHash,
            Model = model,
            Confidence = extraction.Confidence,
            Extraction = extraction,
            ComputedTotalInclTax = validation.ComputedTotalInclTax,
        };

    public static ExtractionOutcome ReviewRequired(
        string documentHash, string model, ModelExtraction extraction, ExtractionValidation validation)
        => new()
        {
            Status = ExtractionStatus.ReviewRequired,
            DocumentHash = documentHash,
            Model = model,
            Confidence = extraction.Confidence,
            Extraction = extraction,
            ComputedTotalInclTax = validation.ComputedTotalInclTax,
        };

    public static ExtractionOutcome Rejected(
        string documentHash, string model, double confidence, ExtractionValidation validation)
        => new()
        {
            Status = ExtractionStatus.Rejected,
            DocumentHash = documentHash,
            Model = model,
            Confidence = confidence,
            Extraction = null,
            Violations = validation.Violations,
        };
}
