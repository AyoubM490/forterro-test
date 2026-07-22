using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Forterro.BuildingBlocks.Observability;
using Forterro.BuildingBlocks.Outbox;
using Forterro.Contracts;
using Forterro.Payments.Worker.Domain;
using Forterro.Payments.Worker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Forterro.Payments.Worker.Application;

public sealed class SagaOptions
{
    public const string SectionName = "Saga";

    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 5;

    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Compte du groupe qui encaisse. En production, par vendeur.</summary>
    public string CreditorIban { get; set; } = "FR7630001007941234567890185";

    public string CreditorName { get; set; } = "Forterro Group";
}

/// <summary>
/// Moteur de la saga. Une seule methode fait avancer une saga d'un cran ;
/// elle est appelee aussi bien par le consommateur Kafka (premiere tentative)
/// que par le planificateur de reprise. Un seul chemin de code, donc un seul
/// comportement a tester et a raisonner.
/// </summary>
public sealed class PaymentSagaOrchestrator(
    PaymentsDbContext context,
    IOpenBankingClient bank,
    IOutboxWriter outbox,
    IOptions<SagaOptions> options,
    ILogger<PaymentSagaOrchestrator> logger)
{
    private readonly SagaOptions _options = options.Value;

    public async Task StartAsync(InvoiceIssued invoiceIssued, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoiceIssued);

        // L'unicite sur invoice_id protege deja en base ; ce test evite juste
        // une exception previsible sur le chemin nominal.
        var existing = await context.Sagas
            .FirstOrDefaultAsync(s => s.InvoiceId == invoiceIssued.InvoiceId, cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "Saga deja ouverte pour la facture {InvoiceId}, evenement ignore.", invoiceIssued.InvoiceId);
            return;
        }

        var saga = PaymentSaga.Start(invoiceIssued);
        context.Sagas.Add(saga);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Saga {SagaId} ouverte pour la facture {Number} ({Amount} {Currency}).",
            saga.Id, saga.InvoiceNumber, saga.Amount, saga.Currency);

        await AdvanceAsync(saga, cancellationToken);
    }

    /// <summary>
    /// Execute une tentative de paiement et enregistre le resultat.
    ///
    /// Chaque issue possible est traitee explicitement : succes, rejet definitif,
    /// erreur rejouable, statut intermediaire. Pas de branche par defaut silencieuse,
    /// une saga ne doit jamais rester bloquee dans un etat non decide.
    /// </summary>
    public async Task AdvanceAsync(PaymentSaga saga, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(saga);

        using var activity = Telemetry.ActivitySource.StartActivity("payment.saga.advance");
        activity?.SetTag("forterro.saga_id", saga.Id);
        activity?.SetTag("forterro.invoice_id", saga.InvoiceId);
        activity?.SetTag("forterro.attempt", saga.Attempts + 1);

        saga.MarkAttemptStarted();

        // La tentative est persistee AVANT l'appel sortant. Si le worker meurt pendant
        // l'appel banque, on sait qu'une tentative etait en cours et la cle d'idempotence
        // associee reste la meme a la reprise : pas de double debit.
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            var outcome = await bank.InitiateAsync(
                saga.DebtorIban,
                _options.CreditorIban,
                _options.CreditorName,
                saga.Amount,
                saga.Currency,
                saga.PaymentReference,
                $"Facture {saga.InvoiceNumber}",
                saga.IdempotencyKey,
                cancellationToken);

            HandleOutcome(saga, outcome);
        }
        catch (OpenBankingCallException ex)
        {
            if (ex.IsRetryable)
            {
                saga.MarkRetryableFailure(ex.Code, ex.Message, _options.MaxAttempts, _options.BaseRetryDelay);
                logger.LogWarning(
                    "Saga {SagaId} : echec rejouable ({Code}), prochaine tentative a {NextAttempt}.",
                    saga.Id, ex.Code, saga.NextAttemptAt);
            }
            else
            {
                saga.MarkFailed(ex.Code, ex.Message, isRetryable: false);
                logger.LogError("Saga {SagaId} : echec definitif ({Code}).", saga.Id, ex.Code);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Issue INDETERMINEE : bogue de deserialisation, coupure reseau, panne du
            // processus. On ne sait pas si la banque a execute le virement.
            //
            // Sans ce filet, la saga resterait definitivement en AwaitingBank : Kafka a
            // deja commite son offset, et le planificateur de reprise ne regarde pas
            // cet etat. Elle serait perdue silencieusement — le pire des scenarios.
            //
            // Replanifier est sur parce que la cle d'idempotence est stable :
            // si la banque a deja execute, elle rejouera sa reponse au lieu de redebiter.
            saga.MarkRetryableFailure(
                "indeterminate", ex.Message, _options.MaxAttempts, _options.BaseRetryDelay);

            logger.LogError(
                ex,
                "Saga {SagaId} : issue indeterminee, reprise planifiee a {NextAttempt}.",
                saga.Id, saga.NextAttemptAt);
        }

        FlushDomainEvents(saga);
        await context.SaveChangesAsync(cancellationToken);

        Telemetry.BusinessEvents.Add(1,
            new KeyValuePair<string, object?>("contract", "payment-saga"),
            new KeyValuePair<string, object?>("outcome", saga.State.ToString()));
    }

    private void HandleOutcome(PaymentSaga saga, PaymentOutcome outcome)
    {
        switch (outcome.Status)
        {
            case BankPaymentStatus.Settled:
                saga.MarkSettled(
                    outcome.PaymentId,
                    outcome.BankReference ?? outcome.PaymentId,
                    outcome.SettledAt ?? DateTimeOffset.UtcNow);

                logger.LogInformation(
                    "Saga {SagaId} reglee, reference bancaire {Reference}.", saga.Id, outcome.BankReference);
                break;

            case BankPaymentStatus.Rejected:
                saga.MarkFailed(
                    MapRejection(outcome.RejectionCode),
                    outcome.RejectionReason ?? "Rejet de la banque sans motif.",
                    isRetryable: false);
                break;

            case BankPaymentStatus.Received:
            case BankPaymentStatus.InProgress:
            case BankPaymentStatus.PendingSca:
                // La banque a accepte mais n'a pas encore execute. On replanifie une
                // verification : ce n'est pas un echec, la saga reste ouverte.
                saga.MarkRetryableFailure(
                    "awaiting_settlement",
                    $"Statut banque : {outcome.Status}.",
                    _options.MaxAttempts,
                    _options.BaseRetryDelay);
                break;

            default:
                saga.MarkFailed("unknown_status", $"Statut inattendu : {outcome.Status}.", isRetryable: false);
                break;
        }
    }

    private static string MapRejection(string? bankCode) => bankCode switch
    {
        // Codes de rejet ISO 20022 (message pain.002).
        "AM04" => PaymentFailureCodes.InsufficientFunds,
        "AC01" or "AC04" => PaymentFailureCodes.InvalidIban,
        "MS03" => PaymentFailureCodes.Rejected,
        _ => PaymentFailureCodes.Rejected,
    };

    private void FlushDomainEvents(PaymentSaga saga)
    {
        foreach (var domainEvent in saga.DomainEvents)
        {
            outbox.Enqueue(domainEvent);
        }

        saga.ClearDomainEvents();
    }
}
