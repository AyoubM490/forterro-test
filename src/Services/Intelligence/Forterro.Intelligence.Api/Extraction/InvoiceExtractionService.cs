using System.Diagnostics;
using System.Security.Cryptography;
using Forterro.BuildingBlocks.Banking;
using Forterro.BuildingBlocks.Observability;
using Forterro.Intelligence.Api.Models;
using Microsoft.Extensions.Options;

namespace Forterro.Intelligence.Api.Extraction;

/// <summary>
/// Orchestration de l'extraction : inference -> validation metier -> routage par confiance.
///
/// Trois decisions structurantes y sont prises, dans cet ordre, et l'ordre compte :
///
///  1. Le modele propose. Sa sortie n'entre nulle part.
///  2. Le domaine dispose. La validation peut REJETER une extraction que le modele
///     jugeait excellente — c'est precisement le cas d'un IBAN faux annonce a 0,95.
///  3. La confiance ne sert qu'a router, jamais a autoriser. Une extraction valide
///     mais peu sure part en revue humaine ; elle ne s'ecrit pas toute seule.
///
/// Le service ne produit JAMAIS qu'un brouillon. L'emission d'une facture reste un
/// acte humain ou une regle explicite, comme la compensation d'un virement execute
/// reste une intervention humaine dans la saga.
/// </summary>
public sealed class InvoiceExtractionService(
    IModelConnector connector,
    IOptions<ExtractionOptions> options,
    ILogger<InvoiceExtractionService> logger)
{
    private readonly ExtractionOptions _options = options.Value;

    public async Task<ExtractionOutcome> ExtractAsync(
        ReadOnlyMemory<byte> document,
        CancellationToken cancellationToken)
    {
        // Empreinte du document : cle d'idempotence. Le meme PDF soumis deux fois ne
        // doit pas declencher deux inferences — garantie de coherence, et une ligne
        // de facture en moins chez le fournisseur.
        var documentHash = Convert.ToHexStringLower(SHA256.HashData(document.Span));

        using var activity = Telemetry.ActivitySource.StartActivity("invoice.extract");
        activity?.SetTag("forterro.document_hash", documentHash);
        activity?.SetTag("gen_ai.request.model", connector.ModelName);

        var started = Stopwatch.GetTimestamp();
        var extraction = await connector.ExtractAsync(document, cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started);

        activity?.SetTag("gen_ai.response.confidence", extraction.Confidence);
        Telemetry.ExternalCallDuration.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("target", "model"),
            new KeyValuePair<string, object?>("model", connector.ModelName));

        var validation = ExtractionValidator.Validate(extraction);

        if (!validation.IsValid)
        {
            // Cas le plus important du service : le modele etait confiant, le domaine
            // dit non. On journalise les violations, pas la confiance — c'est la regle
            // metier qui a tranche.
            logger.LogWarning(
                "Extraction {Hash} rejetee par la validation metier : {Violations}. Confiance annoncee {Confidence:P0}.",
                documentHash,
                string.Join(", ", validation.Violations),
                extraction.Confidence);

            activity?.SetTag("forterro.extraction.status", "rejected");

            return ExtractionOutcome.Rejected(documentHash, connector.ModelName, extraction.Confidence, validation);
        }

        if (extraction.Confidence < _options.MinimumConfidence)
        {
            logger.LogInformation(
                "Extraction {Hash} valide mais peu sure ({Confidence:P0} < {Threshold:P0}) : revue humaine.",
                documentHash,
                extraction.Confidence,
                _options.MinimumConfidence);

            activity?.SetTag("forterro.extraction.status", "review_required");

            return ExtractionOutcome.ReviewRequired(documentHash, connector.ModelName, extraction, validation);
        }

        logger.LogInformation(
            "Extraction {Hash} acceptee en brouillon : {Total} {Currency}, IBAN {Iban}.",
            documentHash,
            validation.ComputedTotalInclTax,
            extraction.Currency,
            Iban.Mask(extraction.DebtorIban));

        activity?.SetTag("forterro.extraction.status", "draft");
        Telemetry.BusinessEvents.Add(1,
            new KeyValuePair<string, object?>("contract", "invoice.extracted"),
            new KeyValuePair<string, object?>("result", "draft"));

        return ExtractionOutcome.Draft(documentHash, connector.ModelName, extraction, validation);
    }
}

public sealed class ExtractionOptions
{
    public const string SectionName = "Extraction";

    /// <summary>
    /// Seuil en dessous duquel une extraction part en revue humaine.
    ///
    /// La valeur ne se choisit pas au clavier : elle se calibre sur un corpus de
    /// factures annotees. 0,80 est un point de depart prudent, pas un resultat
    /// mesure — et tant que le corpus n'existe pas, aucune autre valeur ne serait
    /// plus justifiee.
    /// </summary>
    public double MinimumConfidence { get; set; } = 0.80;

    /// <summary>Taille maximale acceptee, garde-fou avant de payer une inference.</summary>
    public int MaxDocumentBytes { get; set; } = 10 * 1024 * 1024;
}
