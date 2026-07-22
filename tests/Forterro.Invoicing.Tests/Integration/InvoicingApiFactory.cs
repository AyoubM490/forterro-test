using System.Security.Claims;
using System.Text.Encodings.Web;
using Forterro.BuildingBlocks.Messaging;
using Forterro.BuildingBlocks.Messaging.Kafka;
using Forterro.BuildingBlocks.Outbox;
using Forterro.Invoicing.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace Forterro.Invoicing.Tests.Integration;

/// <summary>
/// Hote de test de l'API, adosse a un VRAI PostgreSQL lance dans un conteneur.
///
/// Pas de provider InMemory : il ne connait ni les transactions, ni les contraintes
/// d'unicite, ni xmin, ni le SQL brut du generateur de numeros. Un test qui passe
/// sur InMemory et casse en production n'a aucune valeur.
///
/// Kafka est en revanche remplace par un publieur en memoire : ce qu'on veut verifier
/// ici, c'est que l'evenement atterrit bien dans l'Outbox de maniere transactionnelle,
/// pas que Confluent.Kafka sait parler a un broker.
/// </summary>
public sealed class InvoicingApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("forterro_invoicing_test")
        .WithUsername("forterro")
        .WithPassword("forterro")
        .Build();

    public CapturingEventPublisher Published { get; } = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoicingDbContext>();
        await context.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    public InvoicingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InvoicingDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        return new InvoicingDbContext(options);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Invoicing"] = _postgres.GetConnectionString(),
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:ConsumerGroupId"] = "invoicing-tests",
                ["Oidc:Authority"] = "https://test-authority.local",
                ["Oidc:Audience"] = "forterro-business-services",
                ["Oidc:RequireHttpsMetadata"] = "false",
                ["Otlp:Endpoint"] = string.Empty,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            RemoveHostedService<KafkaConsumerService>(services);
            RemoveHostedService<OutboxDispatcher<InvoicingDbContext>>(services);
            RemoveHostedService<OutboxCleanupService<InvoicingDbContext>>(services);

            services.RemoveAll<IEventPublisher>();
            services.AddSingleton<IEventPublisher>(Published);

            // Authentification remplacee : on teste les regles d'autorisation
            // (les scopes), pas la capacite de Microsoft a valider une signature JWT.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });

            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        });
    }

    private static void RemoveHostedService<T>(IServiceCollection services)
    {
        var descriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T))
            .ToList();

        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }
}

/// <summary>Capture ce qui serait parti sur Kafka, pour l'asserter dans les tests.</summary>
public sealed class CapturingEventPublisher : IEventPublisher
{
    private readonly List<(string Topic, string Contract, string Payload)> _messages = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<(string Topic, string Contract, string Payload)> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Contexte de trace recu au dernier appel. Permet de verifier que le dispatcher
    /// d'Outbox propage bien le traceparent capture a l'ECRITURE, et non celui de sa
    /// propre boucle de fond.
    /// </summary>
    public string? LastParentTraceParent { get; private set; }

    public Task PublishRawAsync(
        string topic,
        string contractName,
        string partitionKey,
        string payloadJson,
        IReadOnlyDictionary<string, string>? headers = null,
        string? parentTraceParent = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _messages.Add((topic, contractName, payloadJson));
            LastParentTraceParent = parentTraceParent;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Injecte un principal porteur des scopes demandes via l'en-tete <c>X-Test-Scopes</c>.
/// Permet de verifier qu'un appelant sans <c>invoicing:write</c> recoit bien un 403.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string ScopesHeader = "X-Test-Scopes";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ScopesHeader, out var scopes))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "test-client"),
                new Claim("preferred_username", "test-client"),
                new Claim("scope", scopes.ToString()),
            ],
            SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
