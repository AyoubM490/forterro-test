namespace Forterro.Intelligence.Api.Documents;

/// <summary>
/// Convertit un PDF en images.
///
/// Cette etape n'existe QUE parce que le fournisseur est un modele ouvert. Une API
/// managee lit un PDF nativement ; les modeles ouverts prennent des images. C'est le
/// cout, rarement anticipe, du choix d'auto-hebergement — et il se paie en dependance
/// native, pas en lignes de code.
///
/// Abstrait pour deux raisons : le rendu PDF s'appuie sur une bibliotheque native
/// (PDFium), qu'on veut pouvoir remplacer ou neutraliser dans les tests ; et un
/// connecteur qui recevrait deja des images n'a aucune raison de la traverser.
/// </summary>
public interface IDocumentRasterizer
{
    /// <summary>Un PDF commence par « %PDF ». Test sur les octets, jamais sur le nom de fichier.</summary>
    static bool IsPdf(ReadOnlySpan<byte> document)
        => document.Length >= 4
           && document[0] == 0x25 && document[1] == 0x50 && document[2] == 0x44 && document[3] == 0x46;

    /// <summary>
    /// Rend les <paramref name="maxPages"/> premieres pages en PNG.
    ///
    /// Le plafond de pages n'est pas cosmetique : chaque page devient une image de
    /// plusieurs milliers de tokens. Un PDF de 200 pages soumis sans garde-fou ferait
    /// exploser la fenetre de contexte et le cout, pour une facture qui en compte deux.
    /// </summary>
    IReadOnlyList<ReadOnlyMemory<byte>> ToPngPages(
        ReadOnlyMemory<byte> pdf,
        int maxPages,
        CancellationToken cancellationToken);
}

public sealed class RasterizationOptions
{
    public const string SectionName = "Rasterization";

    /// <summary>
    /// 200 ppp : compromis entre lisibilite des petits caracteres (un IBAN en corps 7)
    /// et taille de l'image. En dessous de 150, les chiffres se confondent et le
    /// modele invente ; au-dessus de 300, le cout grimpe sans gain mesure.
    /// </summary>
    public int Dpi { get; set; } = 200;

    /// <summary>Une facture tient en une a trois pages. Au-dela, ce n'est pas une facture.</summary>
    public int MaxPages { get; set; } = 3;
}
