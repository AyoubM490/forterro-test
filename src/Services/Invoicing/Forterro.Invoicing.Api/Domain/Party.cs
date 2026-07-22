using System.Text.RegularExpressions;

namespace Forterro.Invoicing.Api.Domain;

/// <summary>
/// Partie a la transaction (vendeur ou acheteur).
/// Champs alignes sur EN 16931 (norme europeenne de facturation electronique) :
/// c'est ce socle qui permet de generer ensuite du Factur-X ou d'emettre via Peppol.
/// </summary>
public sealed partial record Party
{
    public required string Name { get; init; }

    /// <summary>Numero de TVA intracommunautaire (BT-31 / BT-48).</summary>
    public required string VatId { get; init; }

    public required string CountryCode { get; init; }

    public string? AddressLine { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    /// <summary>
    /// Validation de forme du numero de TVA intracommunautaire.
    /// Volontairement syntaxique : la validation d'existence passe par VIES,
    /// un appel externe qui n'a pas sa place dans l'invariant d'un agregat.
    /// </summary>
    public static bool IsWellFormedVatId(string? vatId)
        => !string.IsNullOrWhiteSpace(vatId) && VatIdPattern().IsMatch(vatId);

    [GeneratedRegex("^[A-Z]{2}[A-Z0-9]{2,13}$", RegexOptions.CultureInvariant)]
    private static partial Regex VatIdPattern();
}
