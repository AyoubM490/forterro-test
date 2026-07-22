using System.Net.Http.Headers;
using System.Text;
using FluentValidation;
using Forterro.BuildingBlocks.Api;
using Forterro.BuildingBlocks.Observability;
using Forterro.BuildingBlocks.Resilience;
using Forterro.BuildingBlocks.Security;
using Forterro.OpenBanking.Api;
using Forterro.OpenBanking.Api.Bank;
using Forterro.OpenBanking.Api.Endpoints;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

const string ServiceName = "openbanking-api";

var builder = WebApplication.CreateBuilder(args);

builder.AddForterroObservability(ServiceName);

builder.Services.AddOptions<BankApiOptions>()
    .Bind(builder.Configuration.GetSection(BankApiOptions.SectionName));

var bankOptions = builder.Configuration.GetSection(BankApiOptions.SectionName).Get<BankApiOptions>()
    ?? new BankApiOptions();

if (bankOptions.UseSimulator)
{
    // Singleton : le simulateur conserve l'etat des paiements entre les requetes.
    builder.Services.AddSingleton<IBankConnector, SimulatedBankConnector>();
}
else
{
    builder.Services.AddHttpClient<IBankConnector, HttpBankConnector>(client =>
    {
        client.BaseAddress = new Uri(bankOptions.BaseUrl);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{bankOptions.ClientId}:{bankOptions.ClientSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        // Le timeout fin est gere par le pipeline Polly ; celui-ci est un garde-fou.
        client.Timeout = TimeSpan.FromSeconds(60);
    })
    .AddBankingResilience();
}

builder.Services.AddForterroAuthentication(builder.Configuration);
builder.Services.AddAuthorizationBuilder()
    .AddScopePolicy(Policies.PaymentsRead, "payments:read", "payments:write")
    .AddScopePolicy(Policies.PaymentsWrite, "payments:write")
    .AddScopePolicy(Policies.AccountsRead, "accounts:read");

builder.Services.AddValidatorsFromAssemblyContaining<InitiatePaymentValidator>();

builder.Services.AddProblemDetails();

// L'ORDRE COMPTE : les handlers sont interroges dans l'ordre d'enregistrement et le
// premier qui retourne true gagne. ForterroExceptionHandler traite TOUTES les exceptions
// (il a une branche par defaut), donc s'il passe en premier, BankExceptionHandler ne
// s'execute jamais : toute erreur bancaire ressort en 500 "internal_error" et perd
// l'extension "retryable" dont la saga a besoin pour decider entre reprise et abandon.
builder.Services.AddExceptionHandler<BankExceptionHandler>();
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
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCorrelationId();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenBankingEndpoints();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

await app.RunAsync();

public partial class Program;
