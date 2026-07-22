using Forterro.Invoicing.Api.Domain;

namespace Forterro.Invoicing.Api.Application;

public sealed record PartyDto(
    string Name,
    string VatId,
    string CountryCode,
    string? AddressLine = null,
    string? PostalCode = null,
    string? City = null);

public sealed record InvoiceLineDto(
    string Description,
    decimal Quantity,
    decimal UnitPriceExclTax,
    decimal VatRate);

public sealed record CreateInvoiceRequest(
    PartyDto Seller,
    PartyDto Buyer,
    string Currency,
    string DebtorIban,
    DateOnly DueDate,
    IReadOnlyList<InvoiceLineDto> Lines);

public sealed record CancelInvoiceRequest(string Reason);

public sealed record InvoiceLineResponse(
    Guid Id,
    string Description,
    decimal Quantity,
    decimal UnitPriceExclTax,
    decimal VatRate,
    decimal AmountExclTax,
    decimal TaxAmount,
    decimal AmountInclTax);

public sealed record InvoiceResponse(
    Guid Id,
    string? Number,
    InvoiceStatus Status,
    PartyDto Seller,
    PartyDto Buyer,
    string Currency,
    string DebtorIban,
    DateOnly DueDate,
    decimal TotalExclTax,
    decimal TotalTax,
    decimal TotalInclTax,
    string PaymentReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? PaidAt,
    string? LastFailureCode,
    IReadOnlyList<InvoiceLineResponse> Lines);

/// <summary>Page de resultats. Pagination par curseur seek, pas par OFFSET.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, int PageSize);

public static class InvoiceMapper
{
    public static InvoiceResponse ToResponse(this Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        return new InvoiceResponse(
            invoice.Id,
            invoice.Number,
            invoice.Status,
            ToDto(invoice.Seller),
            ToDto(invoice.Buyer),
            invoice.Currency,
            // Jamais l'IBAN complet dans une reponse d'API : masque par defaut.
            BuildingBlocks.Banking.Iban.Mask(invoice.DebtorIban),
            invoice.DueDate,
            invoice.TotalExclTax,
            invoice.TotalTax,
            invoice.TotalInclTax,
            invoice.PaymentReference,
            invoice.CreatedAt,
            invoice.IssuedAt,
            invoice.PaidAt,
            invoice.LastFailureCode,
            [.. invoice.Lines.Select(l => new InvoiceLineResponse(
                l.Id, l.Description, l.Quantity, l.UnitPriceExclTax, l.VatRate,
                l.AmountExclTax, l.TaxAmount, l.AmountInclTax))]);
    }

    private static PartyDto ToDto(Party party)
        => new(party.Name, party.VatId, party.CountryCode, party.AddressLine, party.PostalCode, party.City);

    public static Party ToDomain(this PartyDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new Party
        {
            Name = dto.Name,
            VatId = dto.VatId.ToUpperInvariant(),
            CountryCode = dto.CountryCode.ToUpperInvariant(),
            AddressLine = dto.AddressLine,
            PostalCode = dto.PostalCode,
            City = dto.City,
        };
    }
}
