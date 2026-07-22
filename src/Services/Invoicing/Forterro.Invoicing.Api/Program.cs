using FluentValidation;
using Forterro.BuildingBlocks.Api;
using Forterro.BuildingBlocks.DependencyInjection;
using Forterro.BuildingBlocks.Observability;
using Forterro.BuildingBlocks.Security;
using Forterro.Contracts;
using Forterro.Invoicing.Api;
using Forterro.Invoicing.Api.Application;
using Forterro.Invoicing.Api.Application.EventHandlers;
using Forterro.Invoicing.Api.Endpoints;
using Forterro.Invoicing.Api.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

const string ServiceName = "invoicing-api";

var builder = WebApplication.CreateBuilder(args);

builder.AddForterroObservability(ServiceName);

// --- Persistance ---------------------------------------------------------
builder.Services.AddDbContext<InvoicingDbContext>((sp, options) =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Invoicing"),
        npgsql =>
        {
            npgsql.MigrationsHistoryTable("__ef_migrations_history", "invoicing");
            // Rejeu automatique sur erreur transitoire (bascule de primaire RDS, coupure reseau).
            npgsql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(5), null);
        });

    if (builder.Environment.IsDevelopment())
    {
        options.EnableDetailedErrors();
        // Jamais active hors developpement : expose les valeurs de parametres dans les logs.
        options.EnableSensitiveDataLogging();
    }
});

// --- Securite : OAuth 2.0 / OpenID Connect -------------------------------
builder.Services.AddForterroAuthentication(builder.Configuration);
builder.Services.AddAuthorizationBuilder()
    .AddScopePolicy(Policies.InvoicingRead, "invoicing:read", "invoicing:write")
    .AddScopePolicy(Policies.InvoicingWrite, "invoicing:write");

// --- Messaging : contrats, Outbox, consommation --------------------------
builder.Services.AddForterroMessaging(
    builder.Configuration,
    registry => registry.AddBusinessServicesContracts());

builder.Services.AddForterroOutbox<InvoicingDbContext>(builder.Configuration);
builder.Services.AddForterroConsumer<InvoicingDbContext>();
builder.Services.AddIntegrationEventHandler<PaymentSettled, PaymentSettledHandler>();
builder.Services.AddIntegrationEventHandler<PaymentFailed, PaymentFailedHandler>();

// --- Applicatif ----------------------------------------------------------
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IInvoiceNumberGenerator, InvoiceNumberGenerator>();
builder.Services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
builder.Services.AddScoped<IdempotencyFilter>();
builder.Services.AddValidatorsFromAssemblyContaining<CreateInvoiceValidator>();

// --- API -----------------------------------------------------------------
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

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(SwaggerConfiguration.Configure);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<InvoicingDbContext>("postgres", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseCorrelationId();

// Doit venir avant le routage : c'est ce qui permet a IdempotencyFilter
// de relire le corps apres la liaison des parametres.
app.UseRequestBuffering();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.OAuthClientId("forterro-swagger"));
    await app.MigrateDatabaseAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapInvoiceEndpoints();

// Liveness : le process repond. Ne touche PAS la base, sinon une base lente
// fait redemarrer en boucle des pods parfaitement sains.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous();

// Readiness : le service peut reellement traiter du trafic.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

await app.RunAsync();

/// <summary>Point d'entree expose aux tests d'integration (WebApplicationFactory).</summary>
public partial class Program;
