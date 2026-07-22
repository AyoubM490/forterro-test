using System.Text;

namespace Forterro.Intelligence.Api.Models;

/// <summary>
/// Modele simule, DETERMINISTE. Utilise par defaut en local et OBLIGATOIREMENT en CI.
///
/// Un test qui appelle un vrai modele est non deterministe *et* facture. Pire : meme
/// a temperature nulle, aucun fournisseur ne garantit une sortie identique d'une
/// version de modele a l'autre. Un test qui affirme le texte produit par un modele
/// finira donc par casser sans qu'aucun code n'ait change.
///
/// Meme principe que SimulatedBankConnector et ses IBAN reserves : le comportement
/// depend du CONTENU du document, pas d'un tirage. Les scenarios sont reproductibles,
/// et chacun couvre un chemin d'echec reel.
/// </summary>
public sealed class SimulatedModelConnector(ILogger<SimulatedModelConnector> logger) : IModelConnector
{
    /// <summary>Chemin nominal : extraction complete, confiance elevee.</summary>
    public const string NominalMarker = "FACTURE-NOMINALE";

    /// <summary>Document degrade : extraction partielle, confiance basse -> revue humaine.</summary>
    public const string UnreadableMarker = "FACTURE-ILLISIBLE";

    /// <summary>
    /// LE scenario qui compte : le modele est CONFIANT et il a TORT.
    /// Il renvoie un IBAN plausible mais faux au modulo 97. Sans validation metier,
    /// cette facture partirait en paiement vers un compte inexistant.
    /// </summary>
    public const string WrongIbanMarker = "FACTURE-IBAN-FAUX";

    /// <summary>Le modele ne repond pas : echec rejouable, la resilience reprend.</summary>
    public const string UnavailableMarker = "MODELE-INDISPONIBLE";

    /// <summary>Le modele lit le tableau a moitie : le total annonce ne correspond pas aux lignes.</summary>
    public const string TotalMismatchMarker = "FACTURE-TOTAL-FAUX";

    public string ModelName => "simulated";

    public Task<ModelExtraction> ExtractAsync(
        ReadOnlyMemory<byte> document,
        CancellationToken cancellationToken)
    {
        // Les documents de test sont du texte ; un vrai connecteur recevrait un PDF.
        // Cette lecture n'a de sens QUE pour le simulateur, et c'est pourquoi elle
        // vit ici plutot que dans le service.
        var content = Encoding.UTF8.GetString(document.Span);

        if (content.Contains(UnavailableMarker, StringComparison.Ordinal))
        {
            throw new ModelException("model_unavailable", "Modele indisponible.", isRetryable: true);
        }

        var extraction = content switch
        {
            _ when content.Contains(UnreadableMarker, StringComparison.Ordinal) => Unreadable(),
            _ when content.Contains(WrongIbanMarker, StringComparison.Ordinal) => ConfidentlyWrongIban(),
            _ when content.Contains(TotalMismatchMarker, StringComparison.Ordinal) => TotalMismatch(),
            _ => Nominal(),
        };

        logger.LogInformation(
            "Extraction simulee : confiance {Confidence:P0}, {LineCount} ligne(s).",
            extraction.Confidence,
            extraction.Lines.Count);

        return Task.FromResult(extraction);
    }

    private static ModelExtraction Nominal() => new()
    {
        SellerName = "Manufacture Dupont",
        SellerVatId = "FR98765432109",
        BuyerName = "Forterro France",
        BuyerVatId = "FR12345678901",
        Currency = "EUR",
        DebtorIban = "FR7630006000011234567890189",
        DueDate = new DateOnly(2026, 12, 31),
        Lines = [new ModelExtractionLine("Composants usines", 10m, 125m, 0.20m)],
        TotalInclTax = 1500m,
        Confidence = 0.97,
    };

    /// <summary>Scan degrade : le modele signale honnetement qu'il n'a pas tout lu.</summary>
    private static ModelExtraction Unreadable() => new()
    {
        SellerName = "Manufacture D???ont",
        BuyerName = "Forterro France",
        Currency = "EUR",
        DebtorIban = null,
        Lines = [],
        Confidence = 0.31,
    };

    /// <summary>
    /// Confiance 0,95 et IBAN faux. Le seuil de confiance laisserait passer ;
    /// seul le controle modulo 97 arrete cette facture.
    /// </summary>
    private static ModelExtraction ConfidentlyWrongIban() => new()
    {
        SellerName = "Manufacture Dupont",
        BuyerName = "Forterro France",
        Currency = "EUR",
        DebtorIban = "FR7630006000011234567890188",  // dernier chiffre altere
        DueDate = new DateOnly(2026, 12, 31),
        Lines = [new ModelExtractionLine("Composants usines", 10m, 125m, 0.20m)],
        TotalInclTax = 1500m,
        Confidence = 0.95,
    };

    /// <summary>Lignes correctes, total annonce faux : lecture partielle du tableau.</summary>
    private static ModelExtraction TotalMismatch() => new()
    {
        SellerName = "Manufacture Dupont",
        BuyerName = "Forterro France",
        Currency = "EUR",
        DebtorIban = "FR7630006000011234567890189",
        DueDate = new DateOnly(2026, 12, 31),
        Lines = [new ModelExtractionLine("Composants usines", 10m, 125m, 0.20m)],
        TotalInclTax = 9999m,   // ne correspond pas aux lignes
        Confidence = 0.92,
    };
}
