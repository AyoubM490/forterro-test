using Forterro.BuildingBlocks.Messaging;
using Forterro.BuildingBlocks.Outbox;
using Forterro.Contracts;
using Forterro.Invoicing.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Forterro.Invoicing.Api.Application.EventHandlers;

/// <summary>
/// Boucle de retour de la saga : le paiement a abouti, la facture passe a "payee".
///
/// Deux protections contre le rejeu (Kafka est at-least-once) :
///  1. l'inbox filtre les EventId deja vus ;
///  2. <see cref="Domain.Invoice.MarkAsPaid"/> est de toute facon idempotent.
/// La seconde couvre le cas ou l'inbox n'aurait pas ete commitee.
/// </summary>
public sealed class PaymentSettledHandler(
    InvoicingDbContext context,
    IOutboxWriter outbox,
    ILogger<PaymentSettledHandler> logger) : IIntegrationEventHandler<PaymentSettled>
{
    public async Task HandleAsync(PaymentSettled @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var invoice = await context.Invoices
            .FirstOrDefaultAsync(i => i.Id == @event.InvoiceId, cancellationToken);

        if (invoice is null)
        {
            // Ni une erreur ni un cas a ignorer silencieusement : un paiement sans facture
            // est une anomalie de rapprochement qui doit remonter en supervision.
            logger.LogError(
                "PaymentSettled recu pour une facture inconnue {InvoiceId} (paiement {PaymentId}).",
                @event.InvoiceId, @event.PaymentId);
            return;
        }

        invoice.MarkAsPaid(@event.Amount, @event.SettledAt);

        foreach (var domainEvent in invoice.DomainEvents)
        {
            outbox.Enqueue(domainEvent);
        }

        invoice.ClearDomainEvents();

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Facture {Number} encaissee, reference bancaire {BankRef}.",
            invoice.Number, @event.BankReference);
    }
}

public sealed class PaymentFailedHandler(
    InvoicingDbContext context,
    ILogger<PaymentFailedHandler> logger) : IIntegrationEventHandler<PaymentFailed>
{
    public async Task HandleAsync(PaymentFailed @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var invoice = await context.Invoices
            .FirstOrDefaultAsync(i => i.Id == @event.InvoiceId, cancellationToken);

        if (invoice is null)
        {
            logger.LogError("PaymentFailed recu pour une facture inconnue {InvoiceId}.", @event.InvoiceId);
            return;
        }

        // La facture n'est PAS annulee : la creance existe toujours,
        // elle bascule en relance. C'est une decision metier, pas un fallback technique.
        invoice.MarkPaymentFailed(@event.FailureCode);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Paiement en echec pour la facture {Number} : {Code} ({Reason}).",
            invoice.Number, @event.FailureCode, @event.Reason);
    }
}
