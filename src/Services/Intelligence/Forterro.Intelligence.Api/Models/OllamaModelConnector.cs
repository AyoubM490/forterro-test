using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forterro.Intelligence.Api.Documents;
using Forterro.Intelligence.Api.Extraction;
using Microsoft.Extensions.Options;

namespace Forterro.Intelligence.Api.Models;

/// <summary>
/// Connecteur vers un modele OUVERT servi par Ollama, auto-heberge.
///
/// Pourquoi un modele ouvert plutot qu'une API managee : aucune donnee ne sort de
/// l'infrastructure. Une facture porte une raison sociale, un IBAN et des montants ;
/// l'auto-hebergement supprime la question de la residence des donnees au lieu de la
/// deleguer a un contrat de sous-traitance. C'est le cout « des donnees qui sortent »
/// de l'ADR 0008, elimine — au prix de GPU a exploiter.
///
/// Modeles pertinents, tous sous licence Apache 2.0, donc compatibles avec le MIT du
/// depot : Qwen2.5-VL (vision) pour lire une facture scannee, Qwen2.5 (texte) en aval
/// d'un OCR. Attention : les poids Llama sont sous « Llama Community License », qui
/// n'est PAS une licence open source au sens OSI et impose des restrictions d'usage.
///
/// INACTIF par defaut : sans Model configure, la fabrique enregistre le simulateur.
/// Le code est present, teste a la compilation, et s'active par configuration.
/// </summary>
public sealed class OllamaModelConnector(
    HttpClient httpClient,
    IDocumentRasterizer rasterizer,
    IOptions<OllamaOptions> options,
    ILogger<OllamaModelConnector> logger) : IModelConnector
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly OllamaOptions _options = options.Value;

    public string ModelName => _options.Model;

    public async Task<ModelExtraction> ExtractAsync(
        ReadOnlyMemory<byte> document,
        CancellationToken cancellationToken)
    {
        // Les modeles ouverts ne lisent pas le PDF nativement, contrairement aux API
        // managees : on rasterise en amont. Un document deja fourni en image traverse
        // sans transformation.
        IReadOnlyList<ReadOnlyMemory<byte>> pages = IDocumentRasterizer.IsPdf(document.Span)
            ? rasterizer.ToPngPages(document, maxPages: int.MaxValue, cancellationToken)
            : [document];

        if (pages.Count == 0)
        {
            throw new ModelException(
                "empty_document",
                "Le document ne contient aucune page exploitable.",
                isRetryable: false);
        }

        var request = new OllamaChatRequest
        {
            Model = _options.Model,
            Stream = false,
            // LE point cle : le schema contraint le decodage cote serveur.
            // La reponse ne peut pas etre un JSON malforme.
            Format = JsonSerializer.Deserialize<JsonElement>(ExtractionSchema.Json),
            Options = new OllamaGenerationOptions { Temperature = 0 },
            Messages =
            [
                new OllamaMessage
                {
                    Role = "user",
                    Content = ExtractionSchema.Prompt,
                    // Une image par page : le modele voit la facture entiere, pas
                    // seulement sa premiere page.
                    Images = [.. pages.Select(p => Convert.ToBase64String(p.Span))],
                },
            ],
        };

        using var response = await httpClient.PostAsJsonAsync("/api/chat", request, Json, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // 5xx et 429 sont rejouables (modele en cours de chargement, file pleine) ;
            // un 4xx ne le sera jamais — rejouer un modele inexistant est du gaspillage.
            var retryable = (int)response.StatusCode >= 500 || (int)response.StatusCode == 429;

            throw new ModelException(
                $"ollama_http_{(int)response.StatusCode}",
                $"Ollama a repondu {(int)response.StatusCode}.",
                retryable);
        }

        var payload = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(Json, cancellationToken)
            ?? throw new ModelException("ollama_empty_response", "Reponse Ollama illisible.", isRetryable: true);

        logger.LogInformation(
            "Inference {Model} terminee, {Tokens} tokens de sortie.",
            _options.Model,
            payload.EvalCount);

        var content = payload.Message?.Content
            ?? throw new ModelException("ollama_empty_content", "Reponse Ollama sans contenu.", isRetryable: true);

        // Le schema garantit la FORME, jamais la VERACITE. Ce qui sort d'ici part
        // directement dans ExtractionValidator.
        return JsonSerializer.Deserialize<ModelExtraction>(content, Json)
            ?? throw new ModelException("ollama_schema_mismatch", "Sortie non conforme au schema.", isRetryable: false);
    }


    private sealed class OllamaChatRequest
    {
        public required string Model { get; init; }
        public required bool Stream { get; init; }
        public JsonElement Format { get; init; }
        public OllamaGenerationOptions? Options { get; init; }
        public required IReadOnlyList<OllamaMessage> Messages { get; init; }
    }

    private sealed class OllamaGenerationOptions
    {
        public double Temperature { get; init; }
    }

    private sealed class OllamaMessage
    {
        public required string Role { get; init; }
        public required string Content { get; init; }
        public IReadOnlyList<string>? Images { get; init; }
    }

    private sealed class OllamaChatResponse
    {
        public OllamaMessage? Message { get; init; }

        [JsonPropertyName("eval_count")]
        public int EvalCount { get; init; }
    }
}

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; set; } = "http://ollama:11434";

    /// <summary>
    /// Nom du modele, ex. « qwen2.5vl:7b ». VIDE par defaut : c'est ce qui laisse le
    /// simulateur en place. Renseigner ce champ suffit a basculer sur le modele reel,
    /// sans recompiler — c'est tout l'interet de la couche anti-corruption.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Une inference de vision sur CPU se compte en minutes, pas en secondes. Le
    /// defaut HttpClient de 100 s echouerait systematiquement sans GPU.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
}
