using FluentAssertions;
using Forterro.Intelligence.Api.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Forterro.Intelligence.Tests;

/// <summary>
/// Ces tests chargent une bibliotheque NATIVE. Ils echouent donc pour deux raisons
/// tres differentes, et c'est voulu : un bogue dans le code de rendu, ou l'absence du
/// binaire PDFium sur la plateforme d'execution. Le second cas est precisement ce
/// qu'on veut voir tomber en CI plutot qu'en production.
///
/// La CI les execute sur Linux/glibc, alors que l'image de production est Alpine/musl.
/// Ils ne prouvent donc pas a eux seuls que le natif se charge en production : c'est le
/// Dockerfile, avec son publish fixe sur linux-musl-x64, qui repond a cette question.
/// </summary>
public sealed class PdfiumRasterizerTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] LireFactureMinimale()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "facture-minimale.pdf"));

    private static PdfiumRasterizer Rasteriseur(RasterizationOptions? options = null)
        => new(Options.Create(options ?? new RasterizationOptions()), NullLogger<PdfiumRasterizer>.Instance);

    [Fact]
    public void Un_pdf_est_reconnu_a_ses_octets_pas_a_son_nom()
    {
        IDocumentRasterizer.IsPdf(LireFactureMinimale()).Should().BeTrue();
        IDocumentRasterizer.IsPdf(PngSignature).Should().BeFalse();
        IDocumentRasterizer.IsPdf([]).Should().BeFalse();
    }

    [Fact]
    public void Une_page_pdf_devient_un_png()
    {
        var pages = Rasteriseur().ToPngPages(LireFactureMinimale(), maxPages: int.MaxValue, CancellationToken.None);

        pages.Should().HaveCount(1);

        // La signature PNG, pas seulement « c'est non vide » : un encodeur casse
        // renverrait des octets, et le test passerait quand meme.
        pages[0].Span[..8].ToArray().Should().Equal(PngSignature);
    }

    [Fact]
    public void Le_ppp_configure_pilote_la_taille_rendue()
    {
        // La fixture mesure 300x200 points PostScript. A 200 ppp, la largeur attendue
        // est 300 / 72 * 200 ≈ 833 px. On verifie que l'option est reellement
        // transmise a PDFium, et pas ignoree au profit d'un defaut interne.
        var large = Rasteriseur(new RasterizationOptions { Dpi = 200 })
            .ToPngPages(LireFactureMinimale(), int.MaxValue, CancellationToken.None);
        var petit = Rasteriseur(new RasterizationOptions { Dpi = 72 })
            .ToPngPages(LireFactureMinimale(), int.MaxValue, CancellationToken.None);

        LargeurPng(large[0]).Should().BeCloseTo(833, 2);
        LargeurPng(petit[0]).Should().Be(300);
        petit[0].Length.Should().BeLessThan(large[0].Length);
    }

    [Fact]
    public void Le_plafond_de_pages_borne_le_rendu()
    {
        var pages = Rasteriseur(new RasterizationOptions { MaxPages = 0 })
            .ToPngPages(LireFactureMinimale(), int.MaxValue, CancellationToken.None);

        // Zero page : le connecteur transforme ce cas en « empty_document » plutot
        // que d'envoyer un message sans image au modele.
        pages.Should().BeEmpty();
    }

    [Fact]
    public void Un_document_qui_nest_pas_un_pdf_echoue_franchement()
    {
        var action = () => Rasteriseur().ToPngPages(PngSignature, int.MaxValue, CancellationToken.None);

        // On veut une exception, pas une liste vide silencieuse : confondre
        // « document illisible » et « document sans page » masquerait la cause.
        action.Should().Throw<Exception>();
    }

    /// <summary>Largeur lue dans le chunk IHDR : octets 16..19, gros-boutiste.</summary>
    private static int LargeurPng(ReadOnlyMemory<byte> png)
    {
        var s = png.Span;
        return (s[16] << 24) | (s[17] << 16) | (s[18] << 8) | s[19];
    }
}
