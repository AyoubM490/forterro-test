using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;

namespace Forterro.Intelligence.Api.Documents;

/// <summary>
/// Rendu PDF via PDFium (paquet PDFtoImage, licence MIT).
///
/// DEPENDANCE NATIVE — c'est la seule du depot, et une dependance native est le genre
/// de chose qui casse au deploiement, jamais sur le poste de developpement. La
/// question posee etait donc : le binaire se charge-t-il sur l'image Alpine utilisee
/// par les autres services, dont la libc est musl et non glibc ?
///
/// Verifie, pas suppose. bblanchon.PDFium et SkiaSharp livrent tous deux un binaire
/// « linux-musl-x64 » ; la suite de tests passe dans un conteneur Alpine, et une
/// rasterisation executee dans l'image aspnet:9.0-alpine finale, en utilisateur non
/// root, rend la page en 833x555 px. Aucune base Debian separee n'est necessaire :
/// le service reste aligne sur les trois autres.
///
/// Ce qui reste vrai malgre tout : les natifs pesent. Si l'image devenait un probleme,
/// le levier est un publish avec RID fixe, qui n'embarque que « linux-musl-x64 » au
/// lieu des sept plateformes livrees par les paquets.
/// </summary>
public sealed class PdfiumRasterizer(
    IOptions<RasterizationOptions> options,
    ILogger<PdfiumRasterizer> logger) : IDocumentRasterizer
{
    private readonly RasterizationOptions _options = options.Value;

    public IReadOnlyList<ReadOnlyMemory<byte>> ToPngPages(
        ReadOnlyMemory<byte> pdf,
        int maxPages,
        CancellationToken cancellationToken)
    {
        var limit = Math.Min(maxPages, _options.MaxPages);
        var pages = new List<ReadOnlyMemory<byte>>(limit);

        // ToArray() : PDFium veut un tableau contigu, il ne consomme pas de Span.
        var bytes = pdf.ToArray();

        var index = 0;

        // CA1416 : PDFium annonce un support par plateforme (Linux, Windows, macOS,
        // Android, iOS). Le depot traite les avertissements en erreurs, et l'analyseur
        // exige une garde de plateforme pour un projet cible « net9.0 » sans RID.
        // Ce service ne s'execute que dans un conteneur Linux ou sur un poste Windows,
        // tous deux dans la liste supportee — la garde n'aurait rien a garder.
#pragma warning disable CA1416
        foreach (var bitmap in Conversion.ToImages(bytes, options: new RenderOptions(Dpi: _options.Dpi)))
#pragma warning restore CA1416
        {
            using (bitmap)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (index++ >= limit)
                {
                    // On le DIT plutot que de tronquer en silence : une facture dont
                    // la derniere page portait le total serait extraite a moitie,
                    // sans que rien ne le signale.
                    logger.LogWarning(
                        "Document tronque a {Limit} page(s) : les suivantes ne sont pas soumises au modele.",
                        limit);
                    break;
                }

                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                pages.Add(data.ToArray());
            }
        }

        logger.LogInformation("PDF rasterise : {Count} page(s) a {Dpi} ppp.", pages.Count, _options.Dpi);

        return pages;
    }
}
