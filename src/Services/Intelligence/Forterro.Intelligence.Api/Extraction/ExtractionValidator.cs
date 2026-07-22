using Forterro.BuildingBlocks.Banking;
using Forterro.Intelligence.Api.Models;

namespace Forterro.Intelligence.Api.Extraction;

/// <summary>
/// LE MODELE PROPOSE, LE DOMAINE DISPOSE.
///
/// La sortie d'un modele est une ENTREE NON FIABLE, au meme titre qu'un formulaire
/// rempli par un inconnu. Elle est donc revalidee par les memes invariants que le
/// reste du systeme : modulo 97 sur l'IBAN, totaux recalcules, devise controlee.
///
/// Ce validateur est la piece qui rend l'extraction defendable dans un domaine ou
/// l'a-peu-pres coute cher. Sans lui, une confiance de 0,95 suffirait a envoyer un
/// virement vers un compte qui n'existe pas.
///
/// Il est volontairement INDEPENDANT du fournisseur : il ne sait pas quel modele a
/// produit la proposition, et son verdict ne depend pas de la confiance annoncee.
/// </summary>
public static class ExtractionValidator
{
    /// <summary>Tolerance sur la comparaison des totaux, pour absorber les arrondis de TVA.</summary>
    private const decimal TotalTolerance = 0.01m;

    public static ExtractionValidation Validate(ModelExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        var violations = new List<string>();

        if (string.IsNullOrWhiteSpace(extraction.SellerName))
        {
            violations.Add("seller_name_missing");
        }

        if (string.IsNullOrWhiteSpace(extraction.BuyerName))
        {
            violations.Add("buyer_name_missing");
        }

        // Devise : ISO 4217 est un code de 3 lettres. Un modele qui rend "euros"
        // ou "€" n'a pas produit une donnee exploitable.
        if (string.IsNullOrWhiteSpace(extraction.Currency)
            || extraction.Currency.Length != 3
            || !extraction.Currency.All(char.IsAsciiLetterUpper))
        {
            violations.Add("currency_invalid");
        }

        // Le controle qui compte le plus. Le modulo 97 rejette les fautes de frappe
        // et les chiffres transposes — exactement les erreurs qu'une lecture de scan
        // produit. Il ne dit rien de l'existence du compte, et ne pretend pas le dire.
        if (string.IsNullOrWhiteSpace(extraction.DebtorIban))
        {
            violations.Add("iban_missing");
        }
        else if (!Iban.IsValid(extraction.DebtorIban))
        {
            violations.Add("iban_checksum_failed");
        }

        if (extraction.DueDate is null)
        {
            violations.Add("due_date_missing");
        }

        if (extraction.Lines.Count == 0)
        {
            violations.Add("no_lines_extracted");
        }

        decimal computed = 0m;

        foreach (var line in extraction.Lines)
        {
            if (line.Quantity is not > 0m
                || line.UnitPriceExclTax is not >= 0m
                || line.VatRate is not (>= 0m and <= 1m))
            {
                violations.Add("line_values_invalid");
                continue;
            }

            var excl = line.Quantity.Value * line.UnitPriceExclTax.Value;
            computed += excl + (excl * line.VatRate!.Value);
        }

        // On RECALCULE au lieu de faire confiance au total annonce. Une facture
        // scannee dont le tableau est lu a moitie produit des lignes coherentes et
        // un total correct : seule la comparaison des deux revele la lecture partielle.
        if (extraction.TotalInclTax is { } declared
            && !violations.Contains("line_values_invalid")
            && extraction.Lines.Count > 0
            && Math.Abs(declared - computed) > TotalTolerance)
        {
            violations.Add("total_mismatch");
        }

        return new ExtractionValidation(violations.Count == 0, violations, decimal.Round(computed, 2));
    }
}

/// <param name="IsValid">Faux des qu'une seule regle est violee. Il n'y a pas de « presque valide ».</param>
/// <param name="Violations">Codes stables, destines aux journaux et a la file de revue.</param>
/// <param name="ComputedTotalInclTax">Total recalcule a partir des lignes — la seule valeur digne de confiance.</param>
public sealed record ExtractionValidation(
    bool IsValid,
    IReadOnlyList<string> Violations,
    decimal ComputedTotalInclTax);
