using Forterro.BuildingBlocks.Api;
using Forterro.BuildingBlocks.DependencyInjection;
using Forterro.BuildingBlocks.Observability;
using Forterro.BuildingBlocks.Resilience;
using Forterro.BuildingBlocks.Security;
using Forterro.Contracts;
using Forterro.Payments.Worker.Application;
using Forterro.Payments.Worker.Endpoints;
using Forterro.Payments.Worker.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

const string ServiceName = "payments-worker";

var builder = WebApplication.CreateBuilder(args);

builder.AddForterroObservability(ServiceName);

// --- Persistance de l'etat des sagas -------------------------------------
builder.Services.AddDbContext<PaymentsDbContext>(options =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Payments"),
        npgsql =>
        {
            npgsql.MigrationsHistoryTable("__ef_migrations_history", "payments");
            npgsql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(5), null);
        });
});

// --- Appel sortant vers l'API Open Banking -------------------------------
builder.Services.AddOptions<ServiceAuthOptions>()
    .Bind(builder.Configuration.GetSection(ServiceAuthOptions.SectionName));

builder.Services.AddOptions<SagaOptions>()
    .Bind(builder.Configuration.GetSection(SagaOptions.SectionName))
    .ValidateDataAnnotations();

// Client dedie a l'obtention du jeton : il ne doit PAS passer par le handler
// d'authentification, sinon on obtient une recursion infinie.
builder.Services.AddHttpClient("token");

builder.Services.AddTransient<ClientCredentialsTokenHandler>();

builder.Services.AddHttpClient<IOpenBankingClient, OpenBankingClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["OpenBanking:BaseUrl"]!);
    client.Timeout = TimeSpan.FromSeconds(60);
})
.AddHttpMessageHandler<ClientCredentialsTokenHandler>()
.AddBankingResilience();

// --- Messaging -----------------------------------------------------------
builder.Services.AddForterroMessaging(
    builder.Configuration,
    registry => registry.AddBusinessServicesContracts());

builder.Services.AddForterroOutbox<PaymentsDbContext>(builder.Configuration);
builder.Services.AddForterroConsumer<PaymentsDbContext>();
builder.Services.AddIntegrationEventHandler<InvoiceIssued, InvoiceIssuedHandler>();
builder.Services.AddIntegrationEventHandler<InvoiceCancelled, InvoiceCancelledHandler>();

// --- Saga ----------------------------------------------------------------
builder.Services.AddScoped<PaymentSagaOrchestrator>();
builder.Services.AddHostedService<SagaRetryService>();

// --- Lecture de l'etat des sagas -----------------------------------------
// Le worker devient aussi un (tres petit) serveur de ressources : le BFF a besoin de lire
// l'avancement d'un paiement pour l'agreger a la facture. Meme brique d'authentification
// que les autres services, meme exigence de scope.
builder.Services.AddForterroAuthentication(builder.Configuration);
builder.Services.AddAuthorizationBuilder()
    .AddScopePolicy(PaymentPolicies.PaymentsRead, "payments:read", "payments:write");

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ForterroExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = ApiJson.Options.PropertyNamingPolicy;
    options.SerializerOptions.DefaultIgnoreCondition = ApiJson.Options.DefaultIgnoreCondition;

    foreach (var converter in ApiJson.Options.Converters)
    {
        options.SerializerOptions.Converters.Add(converter);
    }
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PaymentsDbContext>("postgres", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseCorrelationId();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
}

app.MapSagaEndpoints();

// Un worker expose quand meme HTTP : sans endpoints de sante,
// Kubernetes n'a aucun moyen de savoir si le pod fonctionne.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

await app.RunAsync();

public partial class Program;
