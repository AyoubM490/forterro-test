using System.Globalization;
using Forterro.BuildingBlocks.Api;
using Forterro.BuildingBlocks.Banking;
using Forterro.BuildingBlocks.Messaging;
using Forterro.Contracts;

namespace Forterro.Invoicing.Api.Domain;

public enum InvoiceStatus
{
    Draft = 0,
    Issued = 1,
    Paid = 2,
    Cancelled = 3,
    PaymentFailed = 4,
}

/// <summary>
/// Racine d'agregat Facture.
///
/// Toutes les transitions passent par des methodes qui verifient l'invariant : il est
/// impossible de mettre une facture dans un etat incoherent en manipulant les proprietes.
/// Les evenements d'integration sont accumules ici et convertis en messages Outbox
/// par la couche applicative, dans la meme transaction que la persistance.
/// </summary>
public sealed class Invoice
{
    private readonly List<InvoiceLine> _lines = [];
    private readonly List<IntegrationEvent> _domainEvents = [];

    private Invoice()
    {
    }

    private Invoice(Party seller, Party buyer, string currency, string debtorIban, DateOnly dueDate)
    {
        Id = Guid.NewGuid();
        Seller = seller;
        Buyer = buyer;
        Currency = currency;
        DebtorIban = debtorIban;
        DueDate = dueDate;
        Status = InvoiceStatus.Draft;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Numero legal, attribue seulement a l'emission (sequence sans trou).</summary>
    public string? Number { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public Party Seller { get; private set; } = null!;

    public Party Buyer { get; private set; } = null!;

    /// <summary>Code devise ISO 4217.</summary>
    public string Currency { get; private set; } = "EUR";

    public string DebtorIban { get; private set; } = string.Empty;

    public DateOnly DueDate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? IssuedAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public string? CancellationReason { get; private set; }

    public string? LastFailureCode { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    public IReadOnlyList<IntegrationEvent> DomainEvents => _domainEvents;

    /// <summary>Jeton de concurrence optimiste (xmin PostgreSQL, gere par la base).</summary>
    public uint RowVersion { get; private set; }

    public decimal TotalExclTax => _lines.Sum(l => l.AmountExclTax);

    public decimal TotalTax => _lines.Sum(l => l.TaxAmount);

    public decimal TotalInclTax => TotalExclTax + TotalTax;

    /// <summary>Reference de paiement structuree reportee sur le virement.</summary>
    public string PaymentReference => $"RF{Id.ToString("N")[..16].ToUpperInvariant()}";

    public static Invoice CreateDraft(
        Party seller,
        Party buyer,
        string currency,
        string debtorIban,
        DateOnly dueDate)
    {
        ArgumentNullException.ThrowIfNull(seller);
        ArgumentNullException.ThrowIfNull(buyer);

        if (!Party.IsWellFormedVatId(seller.VatId))
        {
            throw new BusinessRuleException("invalid_seller_vat", $"Numero de TVA vendeur invalide : '{seller.VatId}'.");
        }

        if (!Party.IsWellFormedVatId(buyer.VatId))
        {
            throw new BusinessRuleException("invalid_buyer_vat", $"Numero de TVA acheteur invalide : '{buyer.VatId}'.");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            throw new BusinessRuleException("invalid_currency", "La devise doit etre un code ISO 4217 a 3 lettres.");
        }

        if (!Iban.IsValid(debtorIban))
        {
            throw new BusinessRuleException("invalid_iban", $"IBAN debiteur invalide : '{debtorIban}'.");
        }

        if (dueDate < DateOnly.FromDateTime(DateTime.UtcNow))
        {
            throw new BusinessRuleException("due_date_in_past", "L'echeance ne peut pas etre dans le passe.");
        }

        return new Invoice(seller, buyer, currency.ToUpperInvariant(), Iban.Normalize(debtorIban), dueDate);
    }

    public InvoiceLine AddLine(string description, decimal quantity, decimal unitPriceExclTax, decimal vatRate)
    {
        RequireStatus(InvoiceStatus.Draft, "Seule une facture en brouillon peut etre modifiee.");

        var line = new InvoiceLine(description, quantity, unitPriceExclTax, vatRate);
        _lines.Add(line);
        return line;
    }

    public void RemoveLine(Guid lineId)
    {
        RequireStatus(InvoiceStatus.Draft, "Seule une facture en brouillon peut etre modifiee.");

        var line = _lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new ResourceNotFoundException("Ligne de facture", lineId);

        _lines.Remove(line);
    }

    /// <summary>
    /// Emission : la facture devient un document legal. Irreversible
    /// (on annule par avoir, on ne repasse jamais en brouillon).
    /// </summary>
    public void Issue(string invoiceNumber)
    {
        RequireStatus(InvoiceStatus.Draft, "Cette facture a deja ete emise.");

        if (_lines.Count == 0)
        {
            throw new BusinessRuleException("empty_invoice", "Une facture sans ligne ne peut pas etre emise.");
        }

        if (TotalInclTax <= 0)
        {
            throw new BusinessRuleException("non_positive_total", "Le total d'une facture emise doit etre strictement positif.");
        }

        Number = invoiceNumber;
        Status = InvoiceStatus.Issued;
        IssuedAt = DateTimeOffset.UtcNow;

        _domainEvents.Add(new InvoiceIssued
        {
            InvoiceId = Id,
            InvoiceNumber = invoiceNumber,
            SellerVatId = Seller.VatId,
            BuyerVatId = Buyer.VatId,
            DebtorIban = DebtorIban,
            TotalInclTax = TotalInclTax,
            Currency = Currency,
            DueDate = DueDate,
            PaymentReference = PaymentReference,
        });
    }

    /// <summary>
    /// Encaissement confirme par la saga de paiement.
    /// Idempotent : rejouer <see cref="PaymentSettled"/> ne reemet pas d'evenement.
    /// </summary>
    public void MarkAsPaid(decimal amountPaid, DateTimeOffset paidAt)
    {
        if (Status == InvoiceStatus.Paid)
        {
            return;
        }

        if (Status is not (InvoiceStatus.Issued or InvoiceStatus.PaymentFailed))
        {
            throw new InvalidStateTransitionException(
                $"Une facture au statut {Status} ne peut pas etre marquee payee.");
        }

        if (amountPaid != TotalInclTax)
        {
            throw new BusinessRuleException(
                "amount_mismatch",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Montant encaisse {amountPaid} different du total facture {TotalInclTax}."));
        }

        Status = InvoiceStatus.Paid;
        PaidAt = paidAt;
        LastFailureCode = null;

        _domainEvents.Add(new InvoicePaid
        {
            InvoiceId = Id,
            InvoiceNumber = Number!,
            AmountPaid = amountPaid,
            Currency = Currency,
            PaidAt = paidAt,
        });
    }

    /// <summary>
    /// Echec definitif du paiement. La facture reste due : on ne l'annule pas,
    /// on la marque pour relance. C'est une decision metier, pas technique.
    /// </summary>
    public void MarkPaymentFailed(string failureCode)
    {
        if (Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled)
        {
            return;
        }

        Status = InvoiceStatus.PaymentFailed;
        LastFailureCode = failureCode;
    }

    public void Cancel(string reason)
    {
        if (Status == InvoiceStatus.Paid)
        {
            throw new InvalidStateTransitionException(
                "Une facture payee ne s'annule pas : il faut emettre un avoir.");
        }

        if (Status == InvoiceStatus.Cancelled)
        {
            return;
        }

        var wasIssued = Status == InvoiceStatus.Issued || Status == InvoiceStatus.PaymentFailed;

        Status = InvoiceStatus.Cancelled;
        CancellationReason = reason;

        // On ne notifie que si la facture avait ete rendue publique.
        if (wasIssued)
        {
            _domainEvents.Add(new InvoiceCancelled
            {
                InvoiceId = Id,
                InvoiceNumber = Number!,
                Reason = reason,
            });
        }
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    private void RequireStatus(InvoiceStatus expected, string message)
    {
        if (Status != expected)
        {
            throw new InvalidStateTransitionException($"{message} (statut actuel : {Status})");
        }
    }
}
