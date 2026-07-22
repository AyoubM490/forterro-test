using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Forterro.BuildingBlocks.Security;

/// <summary>
/// Exigence OAuth 2.0 sur les scopes.
///
/// Un scope n'est pas un role : le role dit "qui est l'utilisateur", le scope dit
/// "ce que le client a le droit de faire en son nom". Sur des services mutualises
/// consommes par plusieurs produits, c'est le scope qui porte l'autorisation.
/// </summary>
public sealed class ScopeRequirement(params string[] requiredScopes) : IAuthorizationRequirement
{
    public IReadOnlyCollection<string> RequiredScopes { get; } = requiredScopes;
}

public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    // RFC 6749 : la claim "scope" est une liste separee par des espaces.
    // Certains AS (Azure AD v1) emettent "scp". On accepte les deux.
    private static readonly string[] ScopeClaimTypes = ["scope", "scp", "http://schemas.microsoft.com/identity/claims/scope"];

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var granted = ExtractScopes(context.User);

        if (requirement.RequiredScopes.Any(s => granted.Contains(s)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Scopes effectivement portes par le principal, toutes conventions d'emetteur confondues.
    /// Public parce que le BFF les renvoie a l'ecran sur /bff/me : une interface doit pouvoir
    /// masquer un bouton que l'utilisateur n'a pas le droit d'actionner, plutot que de lui
    /// laisser decouvrir un 403.
    /// </summary>
    public static HashSet<string> ExtractScopes(ClaimsPrincipal user)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var claimType in ScopeClaimTypes)
        {
            foreach (var claim in user.FindAll(claimType))
            {
                foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    scopes.Add(scope);
                }
            }
        }

        return scopes;
    }
}
