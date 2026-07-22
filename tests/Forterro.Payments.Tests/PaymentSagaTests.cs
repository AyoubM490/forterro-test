using FluentAssertions;
using Forterro.Contracts;
using Forterro.Payments.Worker.Domain;
using Xunit;

namespace Forterro.Payments.Tests;

public class PaymentSagaTests
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 3;

    [Fact]
    public void Une_saga_demarre_a_l_etat_Started_sans_tentative()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());

        saga.State.Should().Be(SagaState.Started);
        saga.Attempts.Should().Be(0);
        saga.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Le_reglement_publie_PaymentSettled()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());
        saga.MarkAttemptStarted();

        var settledAt = DateTimeOffset.UtcNow;
        saga.MarkSettled("bank-123", "E2E-REF", settledAt);

        saga.State.Should().Be(SagaState.Settled);

        var settled = saga.DomainEvents.OfType<PaymentSettled>().Single();
        settled.InvoiceId.Should().Be(saga.InvoiceId);
        settled.Amount.Should().Be(1200m);
        settled.BankReference.Should().Be("E2E-REF");
        settled.SettledAt.Should().Be(settledAt);
    }

    /// <summary>
    /// Le point le plus sensible du systeme.
    ///
    /// La cle d'idempotence identifie l'OPERATION voulue, pas la tentative en cours.
    /// Si elle changeait a chaque essai, ce scenario provoquerait un double debit :
    /// tentative 1 atteint la banque, la reponse se perd, tentative 2 presente une cle
    /// inconnue, la banque execute un second virement.
    ///
    /// Elle doit donc rester identique sur toute la duree de vie de la saga.
    /// </summary>
    [Fact]
    public void La_cle_d_idempotence_reste_identique_sur_toutes_les_tentatives()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());

        saga.MarkAttemptStarted();
        var cleTentative1 = saga.IdempotencyKey;

        saga.MarkRetryableFailure("bank_unavailable", "503", MaxAttempts, BaseDelay);
        saga.MarkAttemptStarted();
        var cleTentative2 = saga.IdempotencyKey;

        saga.MarkRetryableFailure("indeterminate", "reponse illisible", MaxAttempts, BaseDelay);
        saga.MarkAttemptStarted();
        var cleTentative3 = saga.IdempotencyKey;

        cleTentative2.Should().Be(cleTentative1);
        cleTentative3.Should().Be(cleTentative1);
    }

    /// <summary>
    /// Deux sagas differentes (donc deux paiements voulus differents) ne doivent
    /// jamais partager de cle : sinon la banque rejouerait la reponse de la premiere
    /// et le second paiement ne partirait jamais.
    /// </summary>
    [Fact]
    public void Deux_sagas_distinctes_ont_des_cles_differentes()
    {
        var saga1 = PaymentSaga.Start(BuildInvoiceIssued());
        var saga2 = PaymentSaga.Start(BuildInvoiceIssued());

        saga1.IdempotencyKey.Should().NotBe(saga2.IdempotencyKey);
    }

    /// <summary>
    /// Saga orpheline : le worker a ete tue pendant l'appel bancaire, aucun gestionnaire
    /// d'exception n'a pu s'executer. Le planificateur de reprise doit pouvoir la relancer,
    /// sinon elle est perdue definitivement.
    /// </summary>
    [Fact]
    public void Une_saga_bloquee_en_attente_de_la_banque_peut_etre_reprise()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());
        saga.MarkAttemptStarted();

        saga.State.Should().Be(SagaState.AwaitingBank);

        var act = saga.MarkAttemptStarted;

        act.Should().NotThrow();
        saga.Attempts.Should().Be(2);
    }

    [Fact]
    public void Un_echec_rejouable_planifie_une_reprise_avec_backoff_exponentiel()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());

        saga.MarkAttemptStarted();
        saga.MarkRetryableFailure("bank_unavailable", "503", MaxAttempts, BaseDelay);
        var premierDelai = saga.NextAttemptAt!.Value - DateTimeOffset.UtcNow;

        saga.MarkAttemptStarted();
        saga.MarkRetryableFailure("bank_unavailable", "503", MaxAttempts, BaseDelay);
        var secondDelai = saga.NextAttemptAt!.Value - DateTimeOffset.UtcNow;

        saga.State.Should().Be(SagaState.AwaitingRetry);
        premierDelai.Should().BeCloseTo(BaseDelay, TimeSpan.FromSeconds(2));
        secondDelai.Should().BeCloseTo(BaseDelay * 2, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Une_saga_ne_rejoue_pas_indefiniment()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());

        for (var i = 0; i < MaxAttempts; i++)
        {
            saga.MarkAttemptStarted();
            saga.MarkRetryableFailure("bank_unavailable", "503", MaxAttempts, BaseDelay);
        }

        // Au-dela du plafond, la saga bascule en echec definitif et publie
        // PaymentFailed : l'incident devient visible au lieu de boucler en silence.
        saga.State.Should().Be(SagaState.Failed);
        saga.Attempts.Should().Be(MaxAttempts);
        saga.NextAttemptAt.Should().BeNull();

        var failed = saga.DomainEvents.OfType<PaymentFailed>().Single();
        failed.Reason.Should().Contain("abandon");
    }

    [Fact]
    public void Un_rejet_definitif_publie_PaymentFailed_non_rejouable()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());
        saga.MarkAttemptStarted();

        saga.MarkFailed(PaymentFailureCodes.InsufficientFunds, "Provision insuffisante.", isRetryable: false);

        var failed = saga.DomainEvents.OfType<PaymentFailed>().Single();
        failed.FailureCode.Should().Be(PaymentFailureCodes.InsufficientFunds);
        failed.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public void Un_reglement_deja_enregistre_n_est_pas_republie()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());
        saga.MarkAttemptStarted();

        saga.MarkSettled("bank-1", "REF", DateTimeOffset.UtcNow);
        saga.MarkSettled("bank-1", "REF", DateTimeOffset.UtcNow);

        saga.DomainEvents.OfType<PaymentSettled>().Should().ContainSingle();
    }

    [Fact]
    public void Une_saga_reglee_ne_peut_plus_echouer()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());
        saga.MarkAttemptStarted();
        saga.MarkSettled("bank-1", "REF", DateTimeOffset.UtcNow);

        saga.MarkFailed("late_error", "Arrive trop tard", isRetryable: false);

        saga.State.Should().Be(SagaState.Settled);
        saga.DomainEvents.OfType<PaymentFailed>().Should().BeEmpty();
    }

    [Fact]
    public void Une_annulation_avant_transmission_arrete_la_saga_sans_debit()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());

        var abandonnee = saga.TryAbort("Commande annulee par le client");

        abandonnee.Should().BeTrue();
        saga.State.Should().Be(SagaState.Aborted);
        saga.DomainEvents.Should().BeEmpty();
    }

    /// <summary>
    /// La limite reelle du modele, assumee explicitement : un virement SEPA deja transmis
    /// ne s'annule pas par un rollback. La saga le signale au lieu de le masquer.
    /// </summary>
    [Fact]
    public void Une_annulation_apres_transmission_exige_une_intervention_humaine()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());
        saga.MarkAttemptStarted();

        var abandonnee = saga.TryAbort("Commande annulee");

        abandonnee.Should().BeFalse();
        saga.State.Should().Be(SagaState.Failed);
        saga.FailureCode.Should().Be("compensation_required");
        saga.FailureReason.Should().Contain("remboursement manuel");
    }

    [Fact]
    public void On_ne_peut_pas_lancer_une_tentative_depuis_un_etat_terminal()
    {
        var saga = PaymentSaga.Start(BuildInvoiceIssued());
        saga.MarkAttemptStarted();
        saga.MarkSettled("bank-1", "REF", DateTimeOffset.UtcNow);

        var act = saga.MarkAttemptStarted;

        act.Should().Throw<BuildingBlocks.Api.InvalidStateTransitionException>();
    }

    private static InvoiceIssued BuildInvoiceIssued() => new()
    {
        InvoiceId = Guid.NewGuid(),
        InvoiceNumber = "INV-2026-000001",
        SellerVatId = "FR12345678901",
        BuyerVatId = "FR98765432109",
        DebtorIban = "FR7630006000011234567890189",
        TotalInclTax = 1200m,
        Currency = "EUR",
        DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
        PaymentReference = "RF1234567890ABCDEF",
    };
}
