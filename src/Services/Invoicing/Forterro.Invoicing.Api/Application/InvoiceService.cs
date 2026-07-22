using System.Diagnostics;
using Forterro.BuildingBlocks.Api;
using Forterro.BuildingBlocks.Observability;
using Forterro.BuildingBlocks.Outbox;
using Forterro.Invoicing.Api.Domain;
using Forterro.Invoicing.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Forterro.Invoicing.Api.Application;

public interface IInvoiceService
{
    Task<InvoiceResponse> CreateDraftAsync(CreateInvoiceRequest request, CancellationToken cancellationToken);

    Task<InvoiceResponse> IssueAsync(Guid invoiceId, CancellationToken cancellationToken);

    Task<InvoiceResponse> CancelAsync(Guid invoiceId, string reason, CancellationToken cancellationToken);

    Task<InvoiceResponse> GetAsync(Guid invoiceId, CancellationToken cancellationToken);

    Task<PagedResult<InvoiceResponse>> ListAsync(
        InvoiceStatus? status, int pageSize, string? cursor, CancellationToken cancellationToken);
}

public sealed class InvoiceService(
    InvoicingDbContext context,
    IInvoiceNumberGenerator numberGenerator,
    IOutboxWriter outbox,
    ILogger<InvoiceService> logger) : IInvoiceService
{
    private const int MaxPageSize = 200;

    public async Task<InvoiceResponse> CreateDraftAsync(
        CreateInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invoice = Invoice.CreateDraft(
            request.Seller.ToDomain(),
            request.Buyer.ToDomain(),
            request.Currency,
            request.DebtorIban,
            request.DueDate);

        foreach (var line in request.Lines)
        {
            invoice.AddLine(line.Description, line.Quantity, line.UnitPriceExclTax, line.VatRate);
        }

        context.Invoices.Add(invoice);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Brouillon {InvoiceId} cree pour {BuyerVat}, total {Total} {Currency}.",
            invoice.Id, invoice.Buyer.VatId, invoice.TotalInclTax, invoice.Currency);

        return invoice.ToResponse();
    }

    /// <summary>
    /// Emission de la facture.
    ///
    /// Point central de tout le systeme : la numerotation legale, le changement d'etat
    /// et l'ecriture de l'evenement dans l'Outbox sont dans UNE transaction.
    /// Soit la facture existe avec son numero et l'evenement partira, soit rien ne s'est passe.
    /// Aucun etat intermediaire n'est observable.
    /// </summary>
    public async Task<InvoiceResponse> IssueAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("invoice.issue");
        activity?.SetTag("forterro.invoice_id", invoiceId);

        // EnableRetryOnFailure est actif sur ce DbContext : ouvrir une transaction
        // directement leverait "the configured execution strategy does not support
        // user-initiated transactions". La strategie doit englober TOUTE la transaction,
        // sinon un retour arriere ne rejouerait qu'une partie des operations.
        var strategy = context.Database.CreateExecutionStrategy();

        var invoice = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // Rechargement a l'interieur de la strategie : en cas de rejeu, on repart
            // d'un etat propre plutot que d'une entite deja modifiee par la tentative precedente.
            context.ChangeTracker.Clear();

            var loaded = await LoadAsync(invoiceId, cancellationToken);

            var number = await numberGenerator.NextAsync(loaded.Seller.VatId, cancellationToken);
            loaded.Issue(number);

            PublishDomainEvents(loaded);

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return loaded;
        });

        Telemetry.BusinessEvents.Add(1,
            new KeyValuePair<string, object?>("contract", "invoice-issued"),
            new KeyValuePair<string, object?>("outcome", "success"));

        logger.LogInformation("Facture {Number} emise ({InvoiceId}).", invoice.Number, invoiceId);

        return invoice.ToResponse();
    }

    public async Task<InvoiceResponse> CancelAsync(
        Guid invoiceId,
        string reason,
        CancellationToken cancellationToken)
    {
        var invoice = await LoadAsync(invoiceId, cancellationToken);

        invoice.Cancel(reason);
        PublishDomainEvents(invoice);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Facture {InvoiceId} annulee : {Reason}", invoiceId, reason);

        return invoice.ToResponse();
    }

    public async Task<InvoiceResponse> GetAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await context.Invoices
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw new ResourceNotFoundException("Facture", invoiceId);

        return invoice.ToResponse();
    }

    /// <summary>
    /// Pagination par curseur (keyset) et non par OFFSET : sur une table de facturation
    /// qui grossit en continu, OFFSET 50000 fait scanner 50 000 lignes a chaque page
    /// et decale les resultats des qu'une facture est inseree entre deux appels.
    /// </summary>
    public async Task<PagedResult<InvoiceResponse>> ListAsync(
        InvoiceStatus? status,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = context.Invoices.AsNoTracking().AsQueryable();

        if (status is not null)
        {
            query = query.Where(i => i.Status == status);
        }

        if (TryDecodeCursor(cursor, out var afterCreatedAt, out var afterId))
        {
            query = query.Where(i =>
                i.CreatedAt < afterCreatedAt || (i.CreatedAt == afterCreatedAt && i.Id < afterId));
        }

        var items = await query
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        // On demande un element de plus : sa presence indique qu'il reste une page.
        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(pageSize);
            var last = items[^1];
            nextCursor = EncodeCursor(last.CreatedAt, last.Id);
        }

        return new PagedResult<InvoiceResponse>([.. items.Select(i => i.ToResponse())], nextCursor, pageSize);
    }

    private async Task<Invoice> LoadAsync(Guid invoiceId, CancellationToken cancellationToken)
        => await context.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
           ?? throw new ResourceNotFoundException("Facture", invoiceId);

    private void PublishDomainEvents(Invoice invoice)
    {
        foreach (var domainEvent in invoice.DomainEvents)
        {
            outbox.Enqueue(domainEvent);
        }

        invoice.ClearDomainEvents();
    }

    internal static string EncodeCursor(DateTimeOffset createdAt, Guid id)
        => Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{createdAt.UtcTicks}:{id}"));

    internal static bool TryDecodeCursor(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split(':', 2);

            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParse(parts[1], out id))
            {
                return false;
            }

            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            // Curseur forge ou tronque : on repart du debut plutot que de renvoyer une 500.
            return false;
        }
    }
}
