using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Forterro.Invoicing.Api;

public static class SwaggerConfiguration
{
    /// <summary>
    /// Documente le flux OAuth 2.0 Authorization Code + PKCE dans l'OpenAPI.
    /// Un consommateur voit immediatement quels scopes demander : sans ca,
    /// chaque integration d'une nouvelle ligne produit part en aller-retours par mail.
    /// </summary>
    public static void Configure(SwaggerGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Forterro Invoicing API",
            Version = "v1",
            Description = "Service mutualise de facturation. Contrats EN 16931, publication d'evenements sur Kafka.",
            Contact = new OpenApiContact { Name = "Business Services", Email = "business-services@forterro.example" },
        });

        var authorityBase = "http://localhost:8080/realms/forterro";

        options.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = "OAuth 2.0 / OpenID Connect via Keycloak.",
            Flows = new OpenApiOAuthFlows
            {
                // PKCE obligatoire : le flow implicite est deprecie (OAuth 2.1) et
                // le client secret n'a rien a faire dans un navigateur.
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri($"{authorityBase}/protocol/openid-connect/auth"),
                    TokenUrl = new Uri($"{authorityBase}/protocol/openid-connect/token"),
                    Scopes = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["invoicing:read"] = "Lecture des factures",
                        ["invoicing:write"] = "Creation, emission et annulation de factures",
                    },
                },

                // Machine a machine : c'est ce flow qu'utilisent les autres lignes produit.
                ClientCredentials = new OpenApiOAuthFlow
                {
                    TokenUrl = new Uri($"{authorityBase}/protocol/openid-connect/token"),
                    Scopes = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["invoicing:read"] = "Lecture des factures",
                        ["invoicing:write"] = "Creation, emission et annulation de factures",
                    },
                },
            },
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "oauth2" },
            }] = ["invoicing:read", "invoicing:write"],
        });
    }
}
