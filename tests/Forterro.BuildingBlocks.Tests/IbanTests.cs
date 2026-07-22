using FluentAssertions;
using Forterro.BuildingBlocks.Banking;
using Xunit;

namespace Forterro.BuildingBlocks.Tests;

public class IbanTests
{
    [Theory]
    [InlineData("FR7630006000011234567890189")]
    [InlineData("FR14 2004 1010 0505 0001 3M02 606")]  // avec espaces, comme sur un RIB
    [InlineData("DE89370400440532013000")]
    [InlineData("GB29NWBK60161331926819")]
    [InlineData("MA64011519000001205000534921")]
    public void IsValid_accepte_les_iban_reels(string iban)
        => Iban.IsValid(iban).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FR7630006000011234567890188")]   // cle de controle fausse (dernier chiffre)
    [InlineData("FR763000600001123456789018")]    // trop court pour la France
    [InlineData("XX7630006000011234567890189")]   // pays inexistant, cle invalide
    [InlineData("7630006000011234567890189")]     // pas de code pays
    [InlineData("FR76-3000-6000-0112")]           // caracteres interdits
    public void IsValid_rejette_les_iban_invalides(string? iban)
        => Iban.IsValid(iban).Should().BeFalse();

    [Fact]
    public void IsValid_detecte_une_transposition_de_chiffres()
    {
        // Le cas que la cle modulo 97 est justement concue pour attraper :
        // l'erreur de saisie humaine la plus frequente.
        const string valide = "DE89370400440532013000";
        const string transpose = "DE89370400440532010300";

        Iban.IsValid(valide).Should().BeTrue();
        Iban.IsValid(transpose).Should().BeFalse();
    }

    [Fact]
    public void Normalize_supprime_les_espaces_et_met_en_majuscules()
        => Iban.Normalize("fr76 3000 6000 0112 3456 7890 189")
            .Should().Be("FR7630006000011234567890189");

    [Fact]
    public void Mask_ne_laisse_apparaitre_que_le_debut_et_la_fin()
    {
        var masque = Iban.Mask("FR7630006000011234567890189");

        masque.Should().StartWith("FR76");
        masque.Should().EndWith("0189");
        masque.Should().NotContain("3000600001");
        masque.Should().HaveLength("FR7630006000011234567890189".Length);
    }

    [Fact]
    public void Mask_ne_divulgue_rien_sur_une_entree_trop_courte()
        => Iban.Mask("FR76").Should().Be("****");
}
