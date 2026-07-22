using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Forterro.Bff.Authentication;

/// <summary>
/// Renouvelle le jeton d'acces d'une session navigateur avant qu'il n'expire.
///
/// Un jeton Keycloak vit 5 minutes par defaut ; une session utilisateur en dure 8. Sans
/// rafraichissement, l'utilisateur serait deconnecte toutes les 5 minutes — ou pire, ses
/// requetes remonteraient un 401 depuis un service en aval, sans qu'il puisse comprendre
/// pourquoi. Le rafraichissement se fait ici, une seule fois, avant que la requete ne parte.
/// </summary>
internal sealed class SessionTokenRefresher(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<BffOptions> bffOptions,
    ILogger<SessionTokenRefresher> logger)
{
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = bffOptions.Value;
        var expiresAt = context.Properties.GetTokenValue("expires_at");

        if (expiresAt is null
            || !DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry))
        {
            return;
        }

        if (expiry - DateTimeOffset.UtcNow > options.RefreshThreshold)
        {
            return;
        }

        var refreshToken = context.Properties.GetTokenValue("refresh_token");

        if (string.IsNullOrEmpty(refreshToken))
        {
            logger.LogInformation("Jeton d'acces expire et aucun refresh_token : la session est fermee.");
            await RejectAsync(context);
            return;
        }

        // Aucun verrou ici. Deux requetes paralleles de la meme session peuvent rafraichir
        // en meme temps : c'est benin tant que la rotation des refresh tokens est desactivee
        // (defaut Keycloak, "Revoke Refresh Token" a Off), les deux appels reussissent.
        // Si vous activez la rotation, il faut un verrou DISTRIBUE — un lock en memoire ne
        // suffirait pas avec plusieurs replicas du BFF, et le perdant se retrouverait
        // deconnecte avec un refresh token deja consomme.
        var refreshed = await TryRefreshAsync(refreshToken, context.HttpContext.RequestAborted);

        if (refreshed is null)
        {
            await RejectAsync(context);
            return;
        }

        context.Properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = refreshed.AccessToken },
            new AuthenticationToken { Name = "refresh_token", Value = refreshed.RefreshToken ?? refreshToken },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = DateTimeOffset.UtcNow
                    .AddSeconds(refreshed.ExpiresIn)
                    .ToString("o", CultureInfo.InvariantCulture),
            },
        ]);

        // Declenche RenewAsync sur le ticket store : sans ca, les nouveaux jetons
        // ne survivent pas a la requete en cours.
        context.ShouldRenew = true;

        logger.LogDebug("Jeton d'acces renouvele pour la session en cours.");
    }

    private async Task<TokenResponse?> TryRefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var options = bffOptions.Value;

        try
        {
            // Le point de terminaison vient du document de decouverte, pas d'une URL en dur :
            // le chemin /protocol/openid-connect/token est propre a Keycloak.
            var oidc = oidcOptions.Get(BffAuthentication.OidcScheme);
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(cancellationToken);

            using var client = httpClientFactory.CreateClient(BffAuthentication.TokenHttpClient);
            using var response = await client.PostAsync(
                new Uri(configuration.TokenEndpoint),
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = options.ClientId,
                    ["client_secret"] = options.ClientSecret,
                }),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Cas nominal d'une session trop vieille : le refresh token a expire cote
                // Keycloak. On journalise en Information, pas en Error : ce n'est pas un incident.
                logger.LogInformation(
                    "Rafraichissement refuse par le serveur d'autorisation ({Status}).",
                    (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Serveur d'autorisation injoignable pendant le rafraichissement.");
            return null;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(ex, "Delai depasse pendant le rafraichissement.");
            return null;
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(BffAuthentication.SessionScheme);
    }

    private sealed record TokenResponse
    {
        [JsonPropertyName("access_token")]
        public required string AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
