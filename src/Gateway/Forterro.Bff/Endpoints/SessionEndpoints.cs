using System.Security.Claims;
using Forterro.Bff.Authentication;
using Forterro.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Forterro.Bff.Endpoints;

/// <summary>Cycle de vie de la session navigateur : ouvrir, decrire, fermer.</summary>
internal static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/bff").WithTags("Session");

        group.MapGet("/login", Login)
            .AllowAnonymous()
            .WithSummary("Ouvre le flow authorization code + PKCE.");

        group.MapPost("/logout", LogoutAsync)
            .WithSummary("Ferme la session locale et celle du serveur d'autorisation.");

        group.MapGet("/me", MeAsync)
            .WithSummary("Decrit la session en cours. Ne renvoie jamais de jeton.");

        group.MapGet("/login-failed", () => Results.Problem(
                title: "Authentification interrompue",
                detail: "Le retour du serveur d'autorisation n'a pas abouti. Relancez /bff/login.",
                statusCode: StatusCodes.Status401Unauthorized))
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    private static IResult Login(HttpContext context, IOptions<BffOptions> options, string? returnUrl)
        => Results.Challenge(
            new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl, options.Value) },
            [BffAuthentication.OidcScheme]);

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IOptions<BffOptions> options,
        string? returnUrl)
    {
        var properties = new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl, options.Value) };

        // Les DEUX comptent. Ne fermer que la session locale laisse la session Keycloak ouverte :
        // le prochain /bff/login reconnecte silencieusement l'utilisateur, qui croit s'etre
        // deconnecte. Sur un poste partage, c'est un vrai probleme, pas un detail d'UX.
        await context.SignOutAsync(BffAuthentication.SessionScheme, properties);
        await context.SignOutAsync(BffAuthentication.OidcScheme, properties);

        // La suppression cote store est faite par le handler de cookie via ITicketStore.RemoveAsync :
        // la session serveur disparait, un cookie exfiltre devient inutilisable immediatement.
        return Results.Empty;
    }

    private static async Task<IResult> MeAsync(HttpContext context, ClaimsPrincipal user) => Results.Ok(new
    {
        subject = user.FindFirstValue("sub"),
        name = user.Identity?.Name,
        email = user.FindFirstValue("email"),
        scopes = await ResolveScopesAsync(context, user),
        roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).OrderBy(r => r, StringComparer.Ordinal),
    });

    /// <summary>
    /// Les scopes qui comptent sont ceux du JETON D'ACCES, pas ceux du principal.
    ///
    /// Sur le chemin navigateur, le principal est construit a partir de l'ID token, qui decrit
    /// QUI est l'utilisateur et ne porte pas de claim <c>scope</c>. Lire les scopes la
    /// renverrait systematiquement une liste vide, et une interface qui s'y fierait masquerait
    /// toutes ses actions. Ce sont les scopes du jeton envoye en aval qui determinent ce que
    /// l'appel obtiendra reellement.
    ///
    /// Sur le chemin machine, principal et jeton d'acces sont une seule et meme chose.
    /// </summary>
    private static async Task<IEnumerable<string>> ResolveScopesAsync(HttpContext context, ClaimsPrincipal user)
    {
        if (BffAuthentication.IsMachineRequest(context.Request))
        {
            return ScopeAuthorizationHandler.ExtractScopes(user).Order(StringComparer.Ordinal);
        }

        var accessToken = await context.GetTokenAsync(BffAuthentication.SessionScheme, "access_token");

        if (accessToken is null || !new JsonWebTokenHandler().CanReadToken(accessToken))
        {
            return [];
        }

        // Lecture sans validation, assumee : ce jeton vient de notre propre store, il a deja
        // ete valide a l'emission, et on ne s'en sert ici que pour informer l'affichage.
        // Aucune decision d'autorisation ne repose dessus.
        var token = new JsonWebTokenHandler().ReadJsonWebToken(accessToken);

        return token.Claims
            .Where(c => c.Type is "scope" or "scp")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// N'accepte qu'un chemin local.
    ///
    /// Sans ce filtre, <c>/bff/login?returnUrl=https://evil.example</c> transforme le BFF en
    /// tremplin : l'utilisateur s'authentifie sur un domaine qu'il reconnait, puis atterrit sur
    /// une copie de la page de login qui lui redemande ses identifiants. C'est la faille de
    /// redirection ouverte, et un parametre de retour en est le vecteur classique.
    /// </summary>
    internal static string SafeReturnUrl(string? candidate, BffOptions options)
        => !string.IsNullOrEmpty(candidate)
            && Uri.IsWellFormedUriString(candidate, UriKind.Relative)
            && candidate.StartsWith('/')
            && !candidate.StartsWith("//", StringComparison.Ordinal)
                ? candidate
                : options.DefaultReturnPath;
}
