using Forterro.BuildingBlocks.Api;
using Forterro.BuildingBlocks.Observability;
using Forterro.BuildingBlocks.Security;
using Forterro.Intelligence.Api.Documents;
using Forterro.Intelligence.Api.Endpoints;
using Forterro.Intelligence.Api.Extraction;
using Forterro.Intelligence.Api.Models;
using Forterro.Intelligence.Api.Resilience;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

const string ServiceName = "intelligence-api";

var builder = WebApplication.CreateBuilder(args);

builder.AddForterroObservability(ServiceName);

builder.Services.AddOptions<ExtractionOptions>()
    .Bind(builder.Configuration.GetSection(ExtractionOptions.SectionName));

builder.Services.AddOptions<RasterizationOptions>()
    .Bind(builder.Configuration.GetSection(RasterizationOptions.SectionName));

builder.Services.AddSingleton<IDocumentRasterizer, PdfiumRasterizer>();

builder.Services.AddOptions<OllamaOptions>()
    .Bind(builder.Configuration.GetSection(OllamaOptions.SectionName));

var ollama = builder.Configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>() ?? new OllamaOptions();

// LE choix de fournisseur se fait ICI, par configuration, et nulle part ailleurs.
// Sans modele configure, c'est le simulateur deterministe qui sert — ce qui rend le
// service deployable et testable sans GPU, sans telechargement et sans cle d'API.
if (string.IsNullOrWhiteSpace(ollama.Model))
{
    builder.Services.AddSingleton<IModelConnector, SimulatedModelConnector>();
}
else
{
    // PAS de AddBankingResilience : ses 8 s par tentative tueraient chaque
    // inference. AddInferenceResilience porte les valeurs adaptees aux
    // entrees/sorties longues et met le HttpClient en timeout infini, pour que
    // Polly soit la seule autorite sur les delais.
    builder.Services.AddHttpClient<IModelConnector, OllamaModelConnector>(client =>
    {
        client.BaseAddress = new Uri(ollama.BaseUrl);
    })
    .AddInferenceResilience(ollama.Timeout);
}

builder.Services.AddScoped<InvoiceExtractionService>();

builder.Services.AddForterroAuthentication(builder.Configuration);
builder.Services.AddAuthorizationBuilder()
    .AddScopePolicy(Policies.DocumentsExtract, "documents:extract");

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ForterroExceptionHandler>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCorrelationId();
app.UseAuthentication();
app.UseAuthorization();

app.MapExtractionEndpoints();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

// Trace au demarrage : savoir QUEL modele sert est la premiere question posee
// quand une extraction surprend. Sans cette ligne, il faut lire la configuration
// d'un pod pour repondre.
app.Logger.LogInformation(
    "Service d'extraction demarre. Connecteur : {Model}.",
    app.Services.GetRequiredService<IOptions<OllamaOptions>>().Value.Model is { Length: > 0 } m
        ? $"Ollama ({m})"
        : "simulateur deterministe (aucun modele configure)");

await app.RunAsync();

public partial class Program;
