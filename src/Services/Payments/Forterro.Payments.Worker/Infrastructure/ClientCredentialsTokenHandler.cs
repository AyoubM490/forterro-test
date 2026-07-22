using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Forterro.Payments.Worker.Infrastructure;

public sealed class ServiceAuthOptions
{
    public const string SectionName = "ServiceAuth";

    public string TokenEndpoint { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Scopes demandes pour appeler l'API Open Banking.</summary>
    public string Scope { get; set; } = "payments:write payments:read";
}

/// <summary>
/// Authentification machine a machine (OAuth 2.0 Client Credentials, RFC 6749 §4.4).
///
/// C'est le flow correct entre deux services : pas d'utilisateur, donc pas de
/// Authorization Code, pas de refresh token, et surtout jamais de propagation
/// du token de l'utilisateur final (qui n'aurait ni la bonne audience ni les bons scopes).
///
/// Le jeton est mis en cache jusqu'a 60 s avant expiration : sans ca on demande
/// un token a Keycloak a chaque appel et on en fait un point de contention.
/// </summary>
public sealed class ClientCredentialsTokenHandler(
    IHttpClientFactory httpClientFactory,
    IOptions<ServiceAuthOptions> options,
    ILogger<ClientCredentialsTokenHandler> logger) : DelegatingHandler
{
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ServiceAuthOptions _options = options.Value;

    private string? _accessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetTokenAsync(forceRefresh: false, cancellationToken));

        var response = await base.SendAsync(request, cancellationToken);

        // 401 malgre un token cense etre valide : les cles de signature ont pu tourner
        // cote serveur d'autorisation. Un seul renouvellement force, puis on abandonne
        // (une boucle de retry sur 401 masquerait une erreur de configuration de scopes).
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogWarning("401 recu de l'API Open Banking, renouvellement force du jeton.");
            response.Dispose();

            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetTokenAsync(forceRefresh: true, cancellationToken));

            response = await base.SendAsync(request, cancellationToken);
        }

        return response;
    }

    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
        {
            return _accessToken;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Double verification : pendant l'attente du verrou, un autre appel
            // a peut-etre deja renouvele le jeton.
            if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            {
                return _accessToken;
            }

            using var client = httpClientFactory.CreateClient("token");

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["scope"] = _options.Scope,
            });

            using var response = await client.PostAsync(
                new Uri(_options.TokenEndpoint), content, cancellationToken);

            response.EnsureSuccessStatusCode();

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Reponse de jeton illisible.");

            _accessToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) - ExpiryMargin;

            logger.LogInformation("Jeton de service obtenu, valide {Seconds} s.", token.ExpiresIn);

            return _accessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lock.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);
}
