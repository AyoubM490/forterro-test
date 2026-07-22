using Forterro.BuildingBlocks.Messaging;

namespace Forterro.Contracts;

/// <summary>
/// Une facture a ete emise. Declenche la saga de paiement.
///
/// Note de conception : l'evenement porte les donnees dont les consommateurs ont besoin
/// (montant, IBAN, echeance) et pas la facture entiere. Un evenement "gros" couple
/// les consommateurs a la structure interne de l'emetteur.
/// </summary>
public sealed record InvoiceIssued : IntegrationEvent
{
    public required Guid InvoiceId { get; init; }

    public required string InvoiceNumber { get; init; }

    public required string SellerVatId { get; init; }

    public required string BuyerVatId { get; init; }

    /// <summary>IBAN a debiter. Format valide en amont par le service de facturation.</summary>
    public required string DebtorIban { get; init; }

    public required decimal TotalInclTax { get; init; }

    public required string Currency { get; init; }

    public required DateOnly DueDate { get; init; }

    /// <summary>Reference de paiement structuree, reportee sur le virement.</summary>
    public required string PaymentReference { get; init; }

    public override string PartitionKey => InvoiceId.ToString();
}

public sealed record InvoiceCancelled : IntegrationEvent
{
    public required Guid InvoiceId { get; init; }

    public required string InvoiceNumber { get; init; }

    public required string Reason { get; init; }

    public override string PartitionKey => InvoiceId.ToString();
}

public sealed record InvoicePaid : IntegrationEvent
{
    public required Guid InvoiceId { get; init; }

    public required string InvoiceNumber { get; init; }

    public required decimal AmountPaid { get; init; }

    public required string Currency { get; init; }

    public required DateTimeOffset PaidAt { get; init; }

    public override string PartitionKey => InvoiceId.ToString();
}
