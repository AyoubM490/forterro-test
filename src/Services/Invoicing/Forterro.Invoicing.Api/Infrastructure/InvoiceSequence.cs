using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Forterro.Invoicing.Api.Infrastructure;

/// <summary>Compteur de numerotation, une ligne par (vendeur, annee).</summary>
public sealed class InvoiceSequence
{
    public required string SellerVatId { get; set; }

    public required int Year { get; set; }

    public long LastValue { get; set; }
}

public interface IInvoiceNumberGenerator
{
    Task<string> NextAsync(string sellerVatId, CancellationToken cancellationToken);
}

/// <summary>
/// Numerotation legale : sequence continue, sans trou, par vendeur et par annee.
///
/// Pourquoi pas une SEQUENCE PostgreSQL : une sequence n'est pas transactionnelle,
/// un rollback consomme quand meme la valeur et cree un trou. L'administration fiscale
/// n'accepte pas de trou dans la numerotation.
///
/// Donc : verrou pessimiste (FOR UPDATE) sur la ligne du compteur. Il serialise les
/// emissions d'un meme vendeur, ce qui est exactement la garantie recherchee.
/// </summary>
public sealed class InvoiceNumberGenerator(InvoicingDbContext context) : IInvoiceNumberGenerator
{
    public async Task<string> NextAsync(string sellerVatId, CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;

        // UPDATE ... RETURNING : atomique, un seul aller-retour, verrou tenu
        // jusqu'au commit de la transaction ambiante.
        var next = await context.Database
            .SqlQuery<long>($"""
                INSERT INTO invoicing.invoice_sequences (seller_vat_id, year, last_value)
                VALUES ({sellerVatId}, {year}, 1)
                ON CONFLICT (seller_vat_id, year)
                DO UPDATE SET last_value = invoicing.invoice_sequences.last_value + 1
                RETURNING last_value
                """)
            .ToListAsync(cancellationToken);

        var value = next[0];

        return string.Create(CultureInfo.InvariantCulture, $"INV-{year}-{value:D6}");
    }
}
