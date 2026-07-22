namespace Forterro.Intelligence.Api.Models;

/// <summary>
/// Couche anti-corruption devant les fournisseurs de modeles.
///
/// Meme role que IBankConnector devant les API bancaires : chaque fournisseur a son
/// dialecte (Ollama, vLLM, une API managee), et ce contrat isole le reste du systeme
/// de ces differences. Changer de modele, c'est ecrire une implementation, pas
/// modifier le service d'extraction.
///
/// Le contrat ne parle NI de prompt, NI de tokens, NI de schema JSON : ce sont des
/// details de fournisseur. Il parle de document et de champs extraits.
/// </summary>
public interface IModelConnector
{
    /// <summary>Nom du modele effectivement utilise, journalise avec chaque extraction.</summary>
    string ModelName { get; }

    /// <summary>
    /// Demande l'extraction des champs d'une facture.
    ///
    /// <paramref name="document"/> est le document brut. Le connecteur est responsable
    /// de sa mise en forme pour son fournisseur — rasterisation, encodage base64,
    /// decoupage en pages. Le service appelant n'en sait rien.
    ///
    /// La valeur retournee est une PROPOSITION, jamais une verite : elle passe
    /// obligatoirement par ExtractionValidator avant d'approcher le domaine.
    /// </summary>
    Task<ModelExtraction> ExtractAsync(
        ReadOnlyMemory<byte> document,
        CancellationToken cancellationToken);
}

/// <summary>
/// Sortie brute du modele, avant toute validation metier.
///
/// Tous les champs sont nullables : un modele qui ne trouve pas l'IBAN doit pouvoir
/// le dire, plutot que d'inventer une chaine plausible. Un champ absent est une
/// information ; un champ invente est un incident.
/// </summary>
public sealed record ModelExtraction
{
    public string? SellerName { get; init; }
    public string? SellerVatId { get; init; }
    public string? BuyerName { get; init; }
    public string? BuyerVatId { get; init; }
    public string? Currency { get; init; }
    public string? DebtorIban { get; init; }
    public DateOnly? DueDate { get; init; }
    public IReadOnlyList<ModelExtractionLine> Lines { get; init; } = [];

    /// <summary>
    /// Total annonce PAR LE MODELE. Il n'est pas repris tel quel : le validateur
    /// recalcule la somme des lignes et compare. Un ecart signale une lecture
    /// partielle du tableau, cas le plus frequent sur une facture scannee.
    /// </summary>
    public decimal? TotalInclTax { get; init; }

    /// <summary>
    /// Confiance auto-declaree, entre 0 et 1. A traiter avec prudence : un modele
    /// est mal calibre par nature et peut etre confiant et faux. Elle sert a router
    /// vers la revue humaine, jamais a autoriser une ecriture.
    /// </summary>
    public double Confidence { get; init; }
}

public sealed record ModelExtractionLine(
    string? Description,
    decimal? Quantity,
    decimal? UnitPriceExclTax,
    decimal? VatRate);

/// <summary>
/// Echec du fournisseur de modele.
///
/// <paramref name="IsRetryable"/> distingue ce que la resilience peut rejouer
/// (modele en cours de chargement, service momentanement indisponible) de ce qui
/// ne sert a rien de rejouer (document illisible, modele inexistant). Meme
/// distinction que BankException, et pour la meme raison : sans elle, on rejoue
/// indefiniment une erreur definitive.
/// </summary>
public sealed class ModelException(string code, string message, bool isRetryable)
    : Exception(message)
{
    public string Code { get; } = code;

    public bool IsRetryable { get; } = isRetryable;
}
