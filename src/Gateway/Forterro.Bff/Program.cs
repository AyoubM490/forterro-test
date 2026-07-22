using Forterro.Bff.Authentication;
using Forterro.Bff.Endpoints;
using Forterro.Bff.Infrastructure;
using Forterro.Bff.Proxy;
using Forterro.Bff.RateLimiting;
using Forterro.Bff.Security;
using Forterro.BuildingBlocks.Api;
using Forterro.BuildingBlocks.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

const string ServiceName = "bff";
const string CorsPolicy = "bff-frontend";

var builder = WebApplication.CreateBuilder(args);

builder.AddForterroObservability(ServiceName);

var bffOptions = builder.Configuration.GetSection(BffOptions.SectionName).Get<BffOptions>() ?? new BffOptions();

// --- Sessions ------------------------------------------------------------
// Le store de sessions suit la topologie : avec plusieurs replicas et un cache local, une
// session ouverte sur le pod A est inconnue du pod B, et l'utilisateur est deconnecte des
// que le load balancer change d'avis. Le repli en memoire ne vaut que pour un seul process.
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");

if (string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddDistributedMemoryCache();
}
else
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = "forterro-bff:";
    });

    // Les cles de protection des donnees doivent etre partagees elles aussi. Le cookie de
    // session ne contient qu'une reference, mais il est chiffre : si chaque replica genere
    // son propre trousseau, un cookie emis par le pod A est illisible pour le pod B, et
    // l'utilisateur est renvoye au login une requete sur deux. Le symptome est intermittent
    // et pointe vers l'authentification, alors que la cause est le stockage des cles.
    builder.Services
        .AddDataProtection()
        .SetApplicationName("forterro-bff")
        .PersistKeysToStackExchangeRedis(
            StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString),
            "forterro-bff:data-protection-keys");
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddBffAuthentication(builder.Configuration);

// Tout est ferme par defaut. Un endpoint nouvellement ajoute est protege sans que son auteur
// ait a y penser ; l'ouvrir demande un AllowAnonymous explicite, donc visible en revue.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddBffRateLimiting(builder.Configuration);

// --- Appels sortants -----------------------------------------------------
builder.Services.AddTransient<OutboundTokenHandler>();

AddDownstreamClient<InvoicingClient>(builder, "Downstream:Invoicing");
AddDownstreamClient<PaymentsClient>(builder, "Downstream:Payments");

// --- Proxy ---------------------------------------------------------------
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms<SessionTokenTransformProvider>();

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

if (bffOptions.AllowedOrigins.Count > 0)
{
    // AllowCredentials est indispensable pour que le navigateur joigne le cookie de session,
    // et c'est precisement pour cela que la liste d'origines doit rester explicite :
    // AllowAnyOrigin est d'ailleurs interdit par la specification en presence de credentials.
    builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
        .WithOrigins([.. bffOptions.AllowedOrigins])
        .AllowCredentials()
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCorrelationId();

if (bffOptions.AllowedOrigins.Count > 0)
{
    app.UseCors(CorsPolicy);
}

app.UseAuthentication();

// APRES l'authentification : le filtre doit savoir si la requete s'appuie sur le cookie.
// AVANT l'autorisation et le proxy : une requete forgee ne doit jamais atteindre l'aval.
app.UseMiddleware<AntiForgeryMiddleware>();

app.UseAuthorization();
app.UseRateLimiter();

app.MapSessionEndpoints();
app.MapOverviewEndpoints();

// Le proxy en dernier : ses routes sont des catch-all et masqueraient les endpoints du BFF.
app.MapReverseProxy();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

await app.RunAsync();

static void AddDownstreamClient<TClient>(WebApplicationBuilder builder, string configurationKey)
    where TClient : class
{
    var baseAddress = builder.Configuration[$"{configurationKey}:BaseUrl"]
        ?? throw new InvalidOperationException($"{configurationKey}:BaseUrl est absent de la configuration.");

    builder.Services.AddHttpClient<TClient>(client =>
    {
        client.BaseAddress = new Uri(baseAddress);

        // Garde-fou : le detail des delais est gere par le pipeline de resilience.
        client.Timeout = TimeSpan.FromSeconds(15);
    })
    .AddHttpMessageHandler<OutboundTokenHandler>()
    .AddStandardResilienceHandler();
}

/// <summary>Point d'entree expose aux tests d'integration.</summary>
public partial class Program;
