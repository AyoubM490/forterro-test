using Forterro.BuildingBlocks.Messaging;
using Forterro.Contracts;
using Forterro.Payments.Worker.Domain;
using Forterro.Payments.Worker.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Forterro.Payments.Worker.Application;

/// <summary>Point d'entree de la saga : une facture emise declenche un paiement.</summary>
public sealed class InvoiceIssuedHandler(PaymentSagaOrchestrator orchestrator)
    : IIntegrationEventHandler<InvoiceIssued>
{
    public Task HandleAsync(InvoiceIssued @event, CancellationToken cancellationToken)
        => orchestrator.StartAsync(@event, cancellationToken);
}

/// <summary>
/// Compensation : la facture est annulee alors que la saga est en cours.
///
/// Si l'ordre n'est pas encore parti a la banque, on l'abandonne proprement.
/// S'il est deja transmis, la saga bascule en echec avec le code
/// <c>compensation_required</c> : un virement execute exige une intervention humaine,
/// on ne pretend pas l'annuler automatiquement.
/// </summary>
public sealed class InvoiceCancelledHandler(
    PaymentsDbContext context,
    ILogger<InvoiceCancelledHandler> logger) : IIntegrationEventHandler<InvoiceCancelled>
{
    public async Task HandleAsync(InvoiceCancelled @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var saga = await context.Sagas
            .FirstOrDefaultAsync(s => s.InvoiceId == @event.InvoiceId, cancellationToken);

        if (saga is null)
        {
            logger.LogDebug("Aucune saga pour la facture annulee {InvoiceId}.", @event.InvoiceId);
            return;
        }

        var aborted = saga.TryAbort(@event.Reason);

        await context.SaveChangesAsync(cancellationToken);

        if (aborted)
        {
            logger.LogInformation("Saga {SagaId} abandonnee avant debit.", saga.Id);
        }
        else if (saga.State == SagaState.Failed && saga.FailureCode == "compensation_required")
        {
            logger.LogError(
                "Saga {SagaId} : ordre deja transmis a la banque, remboursement manuel requis pour la facture {Number}.",
                saga.Id, saga.InvoiceNumber);
        }
    }
}
