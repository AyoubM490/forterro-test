using Forterro.BuildingBlocks.Api;
using Forterro.BuildingBlocks.Messaging;
using Forterro.Contracts;

namespace Forterro.Payments.Worker.Domain;

public enum SagaState
{
    /// <summary>Saga creee a la reception de InvoiceIssued, aucun appel banque encore fait.</summary>
    Started = 0,

    /// <summary>Ordre transmis a la banque, en attente de confirmation.</summary>
    AwaitingBank = 1,

    /// <summary>Echec rejouable : nouvelle tentative planifiee a NextAttemptAt.</summary>
    AwaitingRetry = 2,

    /// <summary>Termine avec succes.</summary>
    Settled = 3,

    /// <summary>Echec definitif, compensation effectuee.</summary>
    Failed = 4,

    /// <summary>Facture annulee avant execution : la saga s'arrete sans debit.</summary>
    Aborted = 5,
}

/// <summary>
/// Saga de paiement d'une facture (orchestration).
///
/// Pourquoi l'orchestration plutot que la choregraphie : le processus a des regles de
/// compensation et un echeancier de reprise qui doivent etre lisibles a un seul endroit.
/// En choregraphie, la logique "on a tente 3 fois, la banque est HS, on compense"
/// se retrouve eclatee entre trois services et devient impossible a auditer.
///
/// L'etat est persiste : un redemarrage du worker en plein milieu reprend exactement ou il en etait.
/// </summary>
public sealed class PaymentSaga
{
    private readonly List<IntegrationEvent> _domainEvents = [];

    private PaymentSaga()
    {
    }

    public Guid Id { get; private set; }

    public Guid InvoiceId { get; private set; }

    public string InvoiceNumber { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "EUR";

    public string DebtorIban { get; private set; } = string.Empty;

    public string PaymentReference { get; private set; } = string.Empty;

    public SagaState State { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public string? BankPaymentId { get; private set; }

    public string? BankReference { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public uint RowVersion { get; private set; }

    public IReadOnlyList<IntegrationEvent> DomainEvents => _domainEvents;

    /// <summary>
    /// Cle d'idempotence transmise a la banque. Derivee du SEUL identifiant de saga,
    /// donc stable sur toutes les tentatives.
    ///
    /// C'est le point le plus important du modele. Une cle par tentative
    /// (<c>{sagaId}-{attempt}</c>) parait plus fine, mais elle est dangereuse : si la
    /// premiere tentative a atteint la banque et que seule la REPONSE s'est perdue
    /// (timeout, coupure, reponse illisible), la tentative suivante presente une cle
    /// inconnue de la banque — qui execute donc un SECOND virement.
    ///
    /// Une cle d'idempotence identifie l'operation voulue, pas l'essai en cours.
    /// La saga veut exactement un paiement pour cette facture : une seule cle.
    /// Rejouer devient alors sans risque, et c'est ce qui permet de traiter
    /// une issue indeterminee comme rejouable.
    /// </summary>
    public string IdempotencyKey => $"payment-{Id:N}";

    public static PaymentSaga Start(InvoiceIssued invoiceIssued)
    {
        ArgumentNullException.ThrowIfNull(invoiceIssued);

        var now = DateTimeOffset.UtcNow;

        return new PaymentSaga
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceIssued.InvoiceId,
            InvoiceNumber = invoiceIssued.InvoiceNumber,
            Amount = invoiceIssued.TotalInclTax,
            Currency = invoiceIssued.Currency,
            DebtorIban = invoiceIssued.DebtorIban,
            PaymentReference = invoiceIssued.PaymentReference,
            State = SagaState.Started,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Demarre une tentative d'appel a la banque.
    ///
    /// <see cref="SagaState.AwaitingBank"/> est un etat de depart valide : c'est le cas
    /// d'une saga restee bloquee parce que le worker est mort pendant l'appel sortant.
    /// La reprise est sans danger uniquement parce que <see cref="IdempotencyKey"/>
    /// est stable — la banque rejouera sa reponse au lieu de re-executer le virement.
    /// </summary>
    public void MarkAttemptStarted()
    {
        if (State is not (SagaState.Started or SagaState.AwaitingRetry or SagaState.AwaitingBank))
        {
            throw new InvalidStateTransitionException(
                $"Impossible de lancer une tentative depuis l'etat {State}.");
        }

        Attempts++;
        State = SagaState.AwaitingBank;
        NextAttemptAt = null;
        Touch();
    }

    public void MarkSettled(string bankPaymentId, string bankReference, DateTimeOffset settledAt)
    {
        if (State == SagaState.Settled)
        {
            return;
        }

        BankPaymentId = bankPaymentId;
        BankReference = bankReference;
        State = SagaState.Settled;
        CompletedAt = settledAt;
        FailureCode = null;
        FailureReason = null;
        Touch();

        _domainEvents.Add(new PaymentSettled
        {
            PaymentId = Id,
            InvoiceId = InvoiceId,
            Amount = Amount,
            Currency = Currency,
            BankReference = bankReference,
            SettledAt = settledAt,
        });
    }

    /// <summary>
    /// Echec rejouable : on replanifie avec un backoff exponentiel plafonne.
    /// Au-dela de <paramref name="maxAttempts"/>, on bascule en echec definitif :
    /// une saga qui rejoue indefiniment masque un incident au lieu de le signaler.
    /// </summary>
    public void MarkRetryableFailure(string code, string reason, int maxAttempts, TimeSpan baseDelay)
    {
        if (Attempts >= maxAttempts)
        {
            MarkFailed(code, $"{reason} (abandon apres {Attempts} tentatives)", isRetryable: false);
            return;
        }

        var delay = TimeSpan.FromSeconds(
            Math.Min(baseDelay.TotalSeconds * Math.Pow(2, Attempts - 1), TimeSpan.FromMinutes(30).TotalSeconds));

        State = SagaState.AwaitingRetry;
        NextAttemptAt = DateTimeOffset.UtcNow.Add(delay);
        FailureCode = code;
        FailureReason = reason;
        Touch();
    }

    public void MarkFailed(string code, string reason, bool isRetryable)
    {
        if (State is SagaState.Settled or SagaState.Failed or SagaState.Aborted)
        {
            return;
        }

        State = SagaState.Failed;
        FailureCode = code;
        FailureReason = reason;
        NextAttemptAt = null;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();

        _domainEvents.Add(new PaymentFailed
        {
            PaymentId = Id,
            InvoiceId = InvoiceId,
            FailureCode = code,
            Reason = reason,
            IsRetryable = isRetryable,
        });
    }

    /// <summary>
    /// Compensation : la facture a ete annulee alors que la saga courait.
    ///
    /// Si l'ordre est deja parti a la banque, on ne peut plus l'arreter : un virement
    /// SEPA execute ne s'annule pas unilateralement. On marque alors la saga en echec
    /// pour qu'un humain traite le remboursement. C'est la limite reelle du modele,
    /// pas un cas qu'on peut cacher sous un rollback automatique.
    /// </summary>
    public bool TryAbort(string reason)
    {
        if (State is SagaState.Settled or SagaState.Failed or SagaState.Aborted)
        {
            return false;
        }

        if (State == SagaState.AwaitingBank)
        {
            MarkFailed(
                "compensation_required",
                $"Facture annulee ({reason}) apres transmission de l'ordre : remboursement manuel requis.",
                isRetryable: false);
            return false;
        }

        State = SagaState.Aborted;
        FailureReason = reason;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
        return true;
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}
