using System.Text;
using FluentAssertions;
using Forterro.Intelligence.Api.Extraction;
using Forterro.Intelligence.Api.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Forterro.Intelligence.Tests;

/// <summary>
/// Ces tests n'affirment JAMAIS ce qu'un modele produit.
///
/// Meme a temperature nulle, aucun fournisseur ne garantit une sortie identique d'une
/// version de modele a l'autre : un test qui verifie les mots du modele finit par
/// casser sans qu'aucun code n'ait change. Ce qui est verifie ici, c'est le
/// comportement de la COUCHE DE VALIDATION face a une proposition donnee — et ca,
/// ca doit etre stable pour toujours.
/// </summary>
public sealed class InvoiceExtractionServiceTests
{
    private static InvoiceExtractionService BuildService(double minimumConfidence = 0.80)
    {
        var connector = new SimulatedModelConnector(NullLogger<SimulatedModelConnector>.Instance);

        var options = Options.Create(new ExtractionOptions { MinimumConfidence = minimumConfidence });

        return new InvoiceExtractionService(connector, options, NullLogger<InvoiceExtractionService>.Instance);
    }

    private static ReadOnlyMemory<byte> Document(string marker)
        => Encoding.UTF8.GetBytes($"document de test — {marker}");

    [Fact]
    public async Task Une_facture_lisible_produit_un_BROUILLON_jamais_une_facture_emise()
    {
        var outcome = await BuildService().ExtractAsync(Document(SimulatedModelConnector.NominalMarker), default);

        outcome.Status.Should().Be(ExtractionStatus.Draft);
        outcome.Extraction.Should().NotBeNull();
        outcome.Violations.Should().BeEmpty();
    }

    /// <summary>
    /// LE test qui justifie toute la couche de validation.
    ///
    /// Le modele annonce 0,95 de confiance — bien au-dessus du seuil — et se trompe
    /// sur un seul caractere de l'IBAN. Le routage par confiance laisserait passer.
    /// Seul le controle modulo 97 arrete cette facture, qui partirait sinon en
    /// virement vers un compte inexistant.
    /// </summary>
    [Fact]
    public async Task Un_modele_confiant_mais_faux_est_rejete_par_le_domaine()
    {
        var outcome = await BuildService().ExtractAsync(Document(SimulatedModelConnector.WrongIbanMarker), default);

        outcome.Confidence.Should().BeGreaterThan(0.80, "le modele se dit tres sur de lui");
        outcome.Status.Should().Be(ExtractionStatus.Rejected);
        outcome.Violations.Should().Contain("iban_checksum_failed");

        // Aucune donnee extraite n'est exposee : la transmettre inviterait un
        // appelant a l'utiliser malgre le rejet.
        outcome.Extraction.Should().BeNull();
    }

    /// <summary>
    /// Lignes coherentes, total annonce faux : c'est la signature d'un tableau lu a
    /// moitie sur un scan. Recalculer au lieu de faire confiance est ce qui le revele.
    /// </summary>
    [Fact]
    public async Task Un_total_annonce_incoherent_avec_les_lignes_est_rejete()
    {
        var outcome = await BuildService().ExtractAsync(Document(SimulatedModelConnector.TotalMismatchMarker), default);

        outcome.Status.Should().Be(ExtractionStatus.Rejected);
        outcome.Violations.Should().Contain("total_mismatch");
    }

    [Fact]
    public async Task Une_extraction_peu_sure_part_en_revue_humaine_et_ne_s_ecrit_pas_seule()
    {
        var outcome = await BuildService().ExtractAsync(Document(SimulatedModelConnector.UnreadableMarker), default);

        // Elle echoue d'abord aux invariants (IBAN absent) : le domaine tranche
        // avant que la confiance n'ait son mot a dire.
        outcome.Status.Should().NotBe(ExtractionStatus.Draft);
    }

    [Fact]
    public async Task Un_modele_indisponible_leve_une_erreur_REJOUABLE()
    {
        var act = async () => await BuildService().ExtractAsync(Document(SimulatedModelConnector.UnavailableMarker), default);

        var exception = await act.Should().ThrowAsync<ModelException>();
        exception.Which.IsRetryable.Should().BeTrue("un modele qui charge doit etre rejoue, pas abandonne");
    }

    /// <summary>
    /// L'empreinte est la cle d'idempotence : le meme document ne doit pas declencher
    /// deux inferences. Garantie de coherence, et une ligne de facture en moins.
    /// </summary>
    [Fact]
    public async Task Le_meme_document_produit_la_meme_empreinte()
    {
        var service = BuildService();
        var document = Document(SimulatedModelConnector.NominalMarker);

        var first = await service.ExtractAsync(document, default);
        var second = await service.ExtractAsync(document, default);

        second.DocumentHash.Should().Be(first.DocumentHash);
        first.DocumentHash.Should().HaveLength(64, "SHA-256 en hexadecimal");
    }

    [Fact]
    public async Task Le_total_expose_est_celui_RECALCULE_pas_celui_annonce_par_le_modele()
    {
        var outcome = await BuildService().ExtractAsync(Document(SimulatedModelConnector.NominalMarker), default);

        // 10 x 125 EUR HT, TVA 20 % => 1500 EUR TTC
        outcome.ComputedTotalInclTax.Should().Be(1500m);
    }

    [Fact]
    public async Task Un_seuil_de_confiance_plus_exigeant_bascule_en_revue_humaine()
    {
        // Meme document, meme extraction valide : seul le seuil change.
        var outcome = await BuildService(minimumConfidence: 0.99)
            .ExtractAsync(Document(SimulatedModelConnector.NominalMarker), default);

        outcome.Status.Should().Be(ExtractionStatus.ReviewRequired);
        outcome.Extraction.Should().NotBeNull("une extraction valide reste exploitable par un relecteur");
    }
}
