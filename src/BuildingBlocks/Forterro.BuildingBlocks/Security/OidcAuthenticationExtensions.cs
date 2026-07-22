using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Forterro.BuildingBlocks.Security;

public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>URL du serveur d'autorisation (Keycloak realm, Duende, Cognito...).</summary>
    [Required]
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Emetteur attendu dans le token. En conteneur, l'Authority interne
    /// (http://keycloak:8080/...) differe de l'issuer emis (http://localhost:8080/...) :
    /// il faut pouvoir les dissocier, sinon la validation echoue en local.
    /// </summary>
    public string? ValidIssuer { get; set; }

    /// <summary>Audience de cette API. Un token emis pour un autre service doit etre rejete.</summary>
    [Required]
    public string Audience { get; set; } = string.Empty;

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Tolerance d'horloge. Le defaut de 5 min est trop large pour de l'API interne.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

public static class OidcAuthenticationExtensions
{
    /// <summary>
    /// Cas courant : un service qui n'accepte que des jetons Bearer.
    /// Le schema JWT devient le schema par defaut.
    /// </summary>
    public static IServiceCollection AddForterroAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddForterroJwtBearer(configuration);

        return services;
    }

    /// <summary>
    /// Enregistre la validation de jeton Forterro sur un schema nomme, sans toucher au
    /// schema par defaut.
    ///
    /// Existe pour le BFF, qui fait cohabiter deux schemas (cookie de session pour les
    /// navigateurs, Bearer pour les appels machine) et choisit lequel appliquer par requete.
    /// Dupliquer <see cref="TokenValidationParameters"/> la-bas aurait cree deux definitions
    /// de "jeton valide" qui divergent au premier changement d'audience.
    /// </summary>
    public static AuthenticationBuilder AddForterroJwtBearer(
        this AuthenticationBuilder builder,
        IConfiguration configuration,
        string scheme = JwtBearerDefaults.AuthenticationScheme)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var services = builder.Services;

        services.AddOptions<OidcOptions>()
            .Bind(configuration.GetSection(OidcOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var oidc = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();

        builder
            .AddJwtBearer(scheme, options =>
            {
                options.Authority = oidc.Authority;
                options.Audience = oidc.Audience;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = oidc.ValidIssuer ?? oidc.Authority,
                    ValidateAudience = true,
                    ValidAudience = oidc.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = oidc.ClockSkew,
                    NameClaimType = "preferred_username",
                    RoleClaimType = ClaimTypes.Role,
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = ctx =>
                    {
                        var logger = ctx.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Forterro.Auth");

                        logger.LogWarning(ctx.Exception, "Validation du jeton en echec.");
                        return Task.CompletedTask;
                    },

                    // Aplatit les roles Keycloak (realm_access.roles) en claims standards.
                    OnTokenValidated = ctx =>
                    {
                        if (ctx.Principal?.Identity is ClaimsIdentity identity)
                        {
                            KeycloakClaimsFlattener.Flatten(identity);
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        // TryAddEnumerable : sur un hote qui enregistre deux schemas JWT, un AddSingleton
        // simple ferait tourner le handler de scopes deux fois par autorisation.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAuthorizationHandler, ScopeAuthorizationHandler>());

        return builder;
    }

    /// <summary>Declare une politique exigeant l'un des scopes fournis.</summary>
    public static AuthorizationBuilder AddScopePolicy(
        this AuthorizationBuilder builder,
        string policyName,
        params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddPolicy(policyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new ScopeRequirement(scopes));
        });
    }
}
