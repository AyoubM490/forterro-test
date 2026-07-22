using System.Security.Claims;
using System.Text.Json;

namespace Forterro.BuildingBlocks.Security;

/// <summary>
/// Keycloak imbrique les roles dans des objets JSON (<c>realm_access.roles</c>,
/// <c>resource_access.{client}.roles</c>) que ASP.NET Core ne sait pas lire nativement :
/// <c>[Authorize(Roles = "...")]</c> ne matcherait jamais. On les remonte en claims plates.
/// </summary>
public static class KeycloakClaimsFlattener
{
    public const string RealmAccessClaim = "realm_access";
    public const string ResourceAccessClaim = "resource_access";

    public static void Flatten(ClaimsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        AddRolesFrom(identity, identity.FindFirst(RealmAccessClaim)?.Value, nested: false);

        var resourceAccess = identity.FindFirst(ResourceAccessClaim)?.Value;
        if (!string.IsNullOrEmpty(resourceAccess))
        {
            AddRolesFrom(identity, resourceAccess, nested: true);
        }
    }

    private static void AddRolesFrom(ClaimsIdentity identity, string? json, bool nested)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (nested)
            {
                foreach (var client in document.RootElement.EnumerateObject())
                {
                    AppendRoles(identity, client.Value);
                }
            }
            else
            {
                AppendRoles(identity, document.RootElement);
            }
        }
        catch (JsonException)
        {
            // Un token mal forme ne doit pas faire tomber l'authentification :
            // il sera simplement depourvu de roles, donc non autorise.
        }
    }

    private static void AppendRoles(ClaimsIdentity identity, JsonElement element)
    {
        if (!element.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var role in roles.EnumerateArray())
        {
            var value = role.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, value));
            }
        }
    }
}
