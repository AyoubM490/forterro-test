using FluentAssertions;
using Forterro.BuildingBlocks.Api;
using Forterro.Contracts;
using Forterro.Invoicing.Api.Domain;
using Xunit;

namespace Forterro.Invoicing.Tests.Domain;

public class InvoiceTests
{
    private const string ValidIban = "FR7630006000011234567890189";

    [Fact]
    public void Une_facture_emise_publie_InvoiceIssued_avec_le_bon_montant()
    {
        var invoice = BuildDraft();
        invoice.AddLine("Licence ERP", quantity: 2, unitPriceExclTax: 500m, vatRate: 0.20m);

        invoice.Issue("INV-2026-000001");

        invoice.Status.Should().Be(InvoiceStatus.Issued);
        invoice.DomainEvents.Should().ContainSingle();

        var issued = invoice.DomainEvents.OfType<InvoiceIssued>().Single();
        issued.InvoiceNumber.Should().Be("INV-2026-000001");
        issued.TotalInclTax.Should().Be(1200m);
        issued.DebtorIban.Should().Be(ValidIban);
        issued.PartitionKey.Should().Be(invoice.Id.ToString());
    }

    [Fact]
    public void Le_total_est_arrondi_au_centime_ligne_par_ligne()
    {
        var invoice = BuildDraft();

        // 3 x 33.335 = 100.005 -> 100.01 en arrondi comptable (away from zero).
        // L'arrondi .NET par defaut (ToEven) donnerait 100.00 et ferait diverger
        // le total de l'addition manuelle des lignes.
        invoice.AddLine("Prestation", quantity: 3, unitPriceExclTax: 33.335m, vatRate: 0.20m);

        invoice.TotalExclTax.Should().Be(100.01m);
        invoice.TotalTax.Should().Be(20.00m);
        invoice.TotalInclTax.Should().Be(120.01m);
    }

    [Fact]
    public void Une_facture_sans_ligne_ne_peut_pas_etre_emise()
    {
        var invoice = BuildDraft();

        var act = () => invoice.Issue("INV-2026-000002");

        act.Should().Throw<BusinessRuleException>().Which.Code.Should().Be("empty_invoice");
    }

    [Fact]
    public void Une_facture_deja_emise_ne_peut_pas_etre_reemise()
    {
        var invoice = IssuedInvoice();

        var act = () => invoice.Issue("INV-2026-000003");

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Fact]
    public void Une_facture_emise_n_accepte_plus_de_ligne()
    {
        var invoice = IssuedInvoice();

        var act = () => invoice.AddLine("Ajout tardif", 1, 100m, 0.20m);

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Fact]
    public void Un_iban_invalide_est_refuse_a_la_creation()
    {
        var act = () => Invoice.CreateDraft(
            Seller(), Buyer(), "EUR", "FR7630006000011234567890188", Tomorrow());

        act.Should().Throw<BusinessRuleException>().Which.Code.Should().Be("invalid_iban");
    }

    [Fact]
    public void Un_numero_de_tva_mal_forme_est_refuse()
    {
        var act = () => Invoice.CreateDraft(
            Seller() with { VatId = "123" }, Buyer(), "EUR", ValidIban, Tomorrow());

        act.Should().Throw<BusinessRuleException>().Which.Code.Should().Be("invalid_seller_vat");
    }

    [Fact]
    public void Une_echeance_passee_est_refusee()
    {
        var hier = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

        var act = () => Invoice.CreateDraft(Seller(), Buyer(), "EUR", ValidIban, hier);

        act.Should().Throw<BusinessRuleException>().Which.Code.Should().Be("due_date_in_past");
    }

    [Fact]
    public void Marquer_payee_deux_fois_ne_publie_qu_un_seul_evenement()
    {
        // Kafka livre at-least-once : ce test verrouille l'idempotence
        // qui empeche un double InvoicePaid en aval.
        var invoice = IssuedInvoice();
        var now = DateTimeOffset.UtcNow;

        invoice.MarkAsPaid(1200m, now);
        invoice.MarkAsPaid(1200m, now);

        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.DomainEvents.OfType<InvoicePaid>().Should().ContainSingle();
    }

    [Fact]
    public void Un_montant_encaisse_different_du_total_est_refuse()
    {
        var invoice = IssuedInvoice();

        var act = () => invoice.MarkAsPaid(1199.99m, DateTimeOffset.UtcNow);

        act.Should().Throw<BusinessRuleException>().Which.Code.Should().Be("amount_mismatch");
    }

    [Fact]
    public void Une_facture_payee_ne_peut_pas_etre_annulee()
    {
        var invoice = IssuedInvoice();
        invoice.MarkAsPaid(1200m, DateTimeOffset.UtcNow);

        var act = () => invoice.Cancel("Erreur de saisie");

        // La regle metier : on emet un avoir, on ne supprime pas une facture payee.
        act.Should().Throw<InvalidStateTransitionException>()
            .WithMessage("*avoir*");
    }

    [Fact]
    public void Annuler_un_brouillon_ne_publie_aucun_evenement()
    {
        var invoice = BuildDraft();
        invoice.AddLine("Licence", 1, 100m, 0.20m);

        invoice.Cancel("Devis abandonne");

        // Un brouillon n'a jamais existe pour l'exterieur : rien a notifier.
        invoice.Status.Should().Be(InvoiceStatus.Cancelled);
        invoice.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Annuler_une_facture_emise_publie_InvoiceCancelled()
    {
        var invoice = IssuedInvoice();
        invoice.ClearDomainEvents();

        invoice.Cancel("Commande annulee par le client");

        invoice.DomainEvents.OfType<InvoiceCancelled>().Should().ContainSingle();
    }

    [Fact]
    public void Un_echec_de_paiement_ne_annule_pas_la_facture()
    {
        var invoice = IssuedInvoice();

        invoice.MarkPaymentFailed(PaymentFailureCodes.InsufficientFunds);

        // La creance existe toujours : elle bascule en relance, pas en annulation.
        invoice.Status.Should().Be(InvoiceStatus.PaymentFailed);
        invoice.LastFailureCode.Should().Be(PaymentFailureCodes.InsufficientFunds);
    }

    [Fact]
    public void Une_facture_en_echec_de_paiement_peut_encore_etre_encaissee()
    {
        var invoice = IssuedInvoice();
        invoice.MarkPaymentFailed(PaymentFailureCodes.BankUnavailable);

        invoice.MarkAsPaid(1200m, DateTimeOffset.UtcNow);

        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.LastFailureCode.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Une_quantite_non_positive_est_refusee(decimal quantity)
    {
        var invoice = BuildDraft();

        var act = () => invoice.AddLine("Prestation", quantity, 100m, 0.20m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Un_taux_de_tva_exprime_en_pourcentage_est_refuse()
    {
        var invoice = BuildDraft();

        // Piege classique : passer 20 au lieu de 0.20 multiplierait la TVA par 100.
        var act = () => invoice.AddLine("Prestation", 1, 100m, vatRate: 20m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void La_reference_de_paiement_est_stable_et_derivee_de_l_identifiant()
    {
        var invoice = BuildDraft();

        var premiere = invoice.PaymentReference;
        var seconde = invoice.PaymentReference;

        premiere.Should().Be(seconde);
        premiere.Should().StartWith("RF").And.HaveLength(18);
    }

    // --- Fabriques de test ------------------------------------------------

    private static DateOnly Tomorrow() => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));

    private static Party Seller() => new()
    {
        Name = "Forterro France",
        VatId = "FR12345678901",
        CountryCode = "FR",
        City = "Lyon",
    };

    private static Party Buyer() => new()
    {
        Name = "Manufacture Dupont",
        VatId = "FR98765432109",
        CountryCode = "FR",
        City = "Grenoble",
    };

    private static Invoice BuildDraft()
        => Invoice.CreateDraft(Seller(), Buyer(), "EUR", ValidIban, Tomorrow());

    private static Invoice IssuedInvoice()
    {
        var invoice = BuildDraft();
        invoice.AddLine("Licence ERP", 2, 500m, 0.20m);
        invoice.Issue("INV-2026-000001");
        return invoice;
    }
}
