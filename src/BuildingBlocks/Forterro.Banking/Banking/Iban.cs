using System.Globalization;
using System.Numerics;

namespace Forterro.BuildingBlocks.Banking;

/// <summary>
/// Validation IBAN (ISO 13616) par cle de controle modulo 97 (ISO 7064 MOD-97-10).
///
/// Pourquoi le faire soi-meme plutot que de laisser la banque rejeter : un IBAN
/// syntaxiquement faux part en virement, revient en rejet 24 a 48 h plus tard,
/// et il faut alors dedebrouiller une saga a moitie executee. On echoue tot, en 400.
/// </summary>
public static class Iban
{
    private const int MinLength = 15;
    private const int MaxLength = 34;

    /// <summary>Longueurs officielles par pays. Un IBAN FR fait 27 caracteres, pas 26 ni 28.</summary>
    private static readonly Dictionary<string, int> CountryLengths = new(StringComparer.Ordinal)
    {
        ["FR"] = 27,
        ["DE"] = 22,
        ["ES"] = 24,
        ["IT"] = 27,
        ["BE"] = 16,
        ["NL"] = 18,
        ["PT"] = 25,
        ["LU"] = 20,
        ["CH"] = 21,
        ["GB"] = 22,
        ["SE"] = 24,
        ["PL"] = 28,
        ["MA"] = 28,
        ["IE"] = 22,
        ["AT"] = 20,
        ["DK"] = 18,
        ["FI"] = 18,
        ["NO"] = 15,
        ["CZ"] = 24,
        ["RO"] = 24,
    };

    public static string Normalize(string? iban)
        => (iban ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    public static bool IsValid(string? iban)
    {
        var value = Normalize(iban);

        if (value.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        if (!char.IsAsciiLetterUpper(value[0]) || !char.IsAsciiLetterUpper(value[1])
            || !char.IsAsciiDigit(value[2]) || !char.IsAsciiDigit(value[3]))
        {
            return false;
        }

        var country = value[..2];
        if (CountryLengths.TryGetValue(country, out var expectedLength) && value.Length != expectedLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return ComputeMod97(value) == 1;
    }

    /// <summary>Masque l'IBAN pour les logs : FR76 **** **** 1234. Aucun IBAN complet en clair.</summary>
    public static string Mask(string? iban)
    {
        var value = Normalize(iban);

        if (value.Length < 8)
        {
            return "****";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value[..4]}{new string('*', value.Length - 8)}{value[^4..]}");
    }

    private static int ComputeMod97(string iban)
    {
        // Les 4 premiers caracteres passent a la fin, puis chaque lettre devient
        // sa position + 9 (A=10 ... Z=35). Le nombre obtenu doit valoir 1 modulo 97.
        var rearranged = string.Concat(iban.AsSpan(4), iban.AsSpan(0, 4));
        var digits = new System.Text.StringBuilder(rearranged.Length * 2);

        foreach (var c in rearranged)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else
            {
                digits.Append((c - 'A' + 10).ToString(CultureInfo.InvariantCulture));
            }
        }

        // BigInteger : un IBAN de 34 caracteres depasse largement un ulong.
        var numeric = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        return (int)(numeric % 97);
    }
}
