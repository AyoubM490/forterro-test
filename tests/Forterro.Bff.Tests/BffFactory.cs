using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Forterro.Bff.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Forterro.Bff.Tests;

/// <summary>
/// Hote de test du BFF. Les services en aval sont remplaces par un stub : ce qu'on verifie
/// ici, c'est la composition et la degradation, pas la capacite de HttpClient a faire un GET.
/// </summary>
public sealed class BffFactory : WebApplicationFactory<Program>
{
    public StubDownstream Invoicing { get; } = new();

    public StubDownstream Payments { get; } = new();

    /// <summary>Autorise un test a durcir la limite de debit pour l'atteindre en quelques appels.</summary>
    public int SessionPermitLimit { get; init; } = 1000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Bff:Authority"] = "https://test-authority.local/realms/forterro",
                ["Bff:ClientId"] = "forterro-bff",
                ["Bff:ClientSecret"] = "test-secret",
                ["Bff:RequireSecureCookie"] = "false",
                ["Oidc:Authority"] = "https://test-authority.local/realms/forterro",
                ["Oidc:Audience"] = "forterro-business-services",
                ["Oidc:RequireHttpsMetadata"] = "false",
                ["Downstream:Invoicing:BaseUrl"] = "http://invoicing.test/",
                ["Downstream:Payments:BaseUrl"] = "http://payments.test/",
                ["RateLimiting:SessionPermitLimit"] = SessionPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Otlp:Endpoint"] = string.Empty,
            }));

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<InvoicingClient>().ConfigurePrimaryHttpMessageHandler(() => Invoicing);
            services.AddHttpClient<PaymentsClient>().ConfigurePrimaryHttpMessageHandler(() => Payments);

            // On teste les regles du BFF, pas la validation de signature JWT par Microsoft.
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
}

/// <summary>Service en aval simule : une reponse programmee par le test.</summary>
public sealed class StubDownstream : HttpMessageHandler
{
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = "{}";

    public int CallCount { get; private set; }

    public string? LastAuthorizationHeader { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        CallCount++;
        LastAuthorizationHeader = request.Headers.Authorization?.ToString();

        return Task.FromResult(new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>Injecte un principal via <c>X-Test-Scopes</c>, comme dans les tests de facturation.</summary>
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
                new Claim("sub", "utilisateur-de-test"),
                new Claim("preferred_username", "demo"),
                new Claim("scope", scopes.ToString()),
            ],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
