namespace Forterro.Invoicing.Api.Domain;

/// <summary>Ligne de facture. Entite enfant : n'existe pas hors de son agregat.</summary>
public sealed class InvoiceLine
{
    // ctor prive : EF Core materialise, le metier passe par Invoice.AddLine.
    private InvoiceLine()
    {
    }

    internal InvoiceLine(string description, decimal quantity, decimal unitPriceExclTax, decimal vatRate)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("La description de ligne est obligatoire.", nameof(description));
        }

        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "La quantite doit etre strictement positive.");
        }

        if (unitPriceExclTax < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPriceExclTax), "Le prix unitaire ne peut pas etre negatif.");
        }

        if (vatRate is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(vatRate), "Le taux de TVA s'exprime en fraction (0.20 pour 20 %).");
        }

        Id = Guid.NewGuid();
        Description = description;
        Quantity = quantity;
        UnitPriceExclTax = unitPriceExclTax;
        VatRate = vatRate;
    }

    public Guid Id { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    public decimal UnitPriceExclTax { get; private set; }

    /// <summary>Taux de TVA en fraction : 0.20 pour 20 %.</summary>
    public decimal VatRate { get; private set; }

    /// <summary>
    /// Arrondi au centime a la ligne (comptable, MidpointRounding.AwayFromZero).
    /// Le defaut .NET est ToEven ("arrondi du banquier") : sur une facture,
    /// il produit un total qui ne correspond pas a l'addition manuelle des lignes.
    /// </summary>
    public decimal AmountExclTax => Math.Round(Quantity * UnitPriceExclTax, 2, MidpointRounding.AwayFromZero);

    public decimal TaxAmount => Math.Round(AmountExclTax * VatRate, 2, MidpointRounding.AwayFromZero);

    public decimal AmountInclTax => AmountExclTax + TaxAmount;
}
