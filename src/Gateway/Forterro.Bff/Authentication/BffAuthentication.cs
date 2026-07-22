using System.Net.Mime;
using System.Security.Claims;
using Forterro.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Forterro.Bff.Authentication;

/// <summary>
/// Les deux publics du BFF et le schema d'authentification de chacun.
///
/// Une plateforme de services mutualises est appelee de deux facons qui n'ont pas les memes
/// contraintes de securite :
///
/// - un ECRAN, dans un navigateur, expose au XSS. Les jetons ne doivent jamais atteindre le
///   JavaScript : session serveur, cookie HttpOnly (<see cref="SessionScheme"/>).
/// - une AUTRE LIGNE PRODUIT, en machine a machine. Il n'y a ni navigateur ni JavaScript, donc
///   ni XSS ni CSRF : le jeton porteur suffit (<see cref="MachineScheme"/>).
///
/// Les deux cohabitent, et c'est <see cref="SelectorScheme"/> qui tranche par requete.
/// </summary>
internal static class BffAuthentication
{
    /// <summary>Schema par defaut : ne valide rien lui-meme, il delegue.</summary>
    public const string SelectorScheme = "forterro";

    /// <summary>Cookie de session navigateur. Ne contient qu'une cle vers le store serveur.</summary>
    public const string SessionScheme = "bff-session";

    /// <summary>Flow authorization code + PKCE. N'intervient que sur /bff/login et le callback.</summary>
    public const string OidcScheme = "bff-oidc";

    /// <summary>Jeton porteur, pour les appels machine a machine.</summary>
    public const string MachineScheme = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>Client HTTP du back-channel. Nomme pour ne pas heriter d'un handler d'auth.</summary>
    public const string TokenHttpClient = "bff-token";

    public static IServiceCollection AddBffAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<BffOptions>()
            .Bind(configuration.GetSection(BffOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ITicketStore, DistributedTicketStore>();
        services.AddScoped<SessionTokenRefresher>();
        services.AddHttpClient(TokenHttpClient);

        services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, "Cookie de session ou jeton porteur", options =>
            {
                // Un en-tete Authorization explicite designe sans ambiguite un appelant machine :
                // un navigateur ne le pose jamais tout seul. Le cookie, lui, est ambiant — c'est
                // exactement ce qui rend le CSRF possible, et pourquoi seul le chemin cookie
                // exige un jeton anti-CSRF (voir AntiForgeryMiddleware).
                options.ForwardDefaultSelector = context =>
                    IsMachineRequest(context.Request) ? MachineScheme : SessionScheme;
            })
            .AddCookie(SessionScheme)
            .AddOpenIdConnect(OidcScheme, _ => { })
            .AddForterroJwtBearer(configuration, MachineScheme);

        // Les deux schemas sont configures PAR INJECTION, pas en lisant IConfiguration ici.
        // Lire la configuration au moment de l'enregistrement fige les valeurs presentes a cet
        // instant : toute source ajoutee ensuite — un fournisseur de secrets, la surcharge d'un
        // hote de test — est ignoree en silence, et le service demarre avec un ClientId vide.
        services.AddOptions<CookieAuthenticationOptions>(SessionScheme)
            .Configure<ITicketStore, IOptions<BffOptions>>((options, store, bff) =>
            {
                options.SessionStore = store;
                ConfigureSessionCookie(options, bff.Value);
            });

        services.AddOptions<OpenIdConnectOptions>(OidcScheme)
            .Configure<IOptions<BffOptions>>((options, bff) => ConfigureOidc(options, bff.Value));

        return services;
    }

    /// <summary>
    /// Distingue un appel machine d'un appel navigateur.
    ///
    /// Volontairement expose : la selection du schema d'authentification et la protection
    /// anti-CSRF doivent partager exactement le meme critere. Si les deux divergeaient un jour,
    /// on obtiendrait une requete authentifiee par cookie mais consideree "machine" par le
    /// filtre CSRF — c'est-a-dire un contournement complet de la protection.
    /// </summary>
    public static bool IsMachineRequest(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Headers.Authorization.Any(
            value => value?.StartsWith("Bearer ", StringComparison.Ordinal) == true);
    }

    private static void ConfigureSessionCookie(CookieAuthenticationOptions options, BffOptions bff)
    {
        // Le prefixe __Host- est une instruction au navigateur : refuse ce cookie s'il n'est
        // pas Secure, pas en Path=/, ou s'il porte un Domain. Il neutralise le "cookie tossing"
        // depuis un sous-domaine compromis. Il exige https, donc pas de prefixe en dev local.
        options.Cookie.Name = bff.RequireSecureCookie ? "__Host-forterro.session" : "forterro.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = bff.RequireSecureCookie
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.Cookie.Path = "/";

        options.ExpireTimeSpan = bff.SessionLifetime;
        options.SlidingExpiration = true;

        options.Events.OnValidatePrincipal = context =>
            context.HttpContext.RequestServices
                .GetRequiredService<SessionTokenRefresher>()
                .ValidateAsync(context);

        // Un appel d'API non authentifie doit recevoir 401, pas une redirection 302 vers
        // Keycloak : une redirection dans un fetch() se solde par une erreur CORS opaque
        // cote client, qui masque completement la vraie cause.
        options.Events.OnRedirectToLogin = context => WriteStatus(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => WriteStatus(context, StatusCodes.Status403Forbidden);
    }

    private static void ConfigureOidc(OpenIdConnectOptions options, BffOptions bff)
    {
        options.Authority = bff.Authority;
        options.ClientId = bff.ClientId;
        options.ClientSecret = bff.ClientSecret;
        options.RequireHttpsMetadata = bff.RequireSecureCookie;

        // Authorization code + PKCE. Le BFF est un client confidentiel : le secret suffirait
        // formellement, mais PKCE ferme en plus l'interception du code sur le canal frontal.
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;

        // LE point du pattern : les jetons sont ranges dans le ticket, donc dans le store
        // serveur. Le navigateur n'en voit aucun.
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;

        options.SignInScheme = SessionScheme;
        options.CallbackPath = "/bff/callback";
        options.SignedOutCallbackPath = "/bff/signout-callback";
        options.SignedOutRedirectUri = bff.DefaultReturnPath;

        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");

        foreach (var scope in bff.Scopes)
        {
            options.Scope.Add(scope);
        }

        // Les cookies de correlation et de nonce voyagent sur le retour depuis Keycloak, qui
        // est une navigation de premier niveau venant d'un AUTRE site. En SameSite=Strict ils
        // ne seraient pas renvoyes et chaque login echouerait sur "Correlation failed" — l'un
        // des messages les plus opaques d'ASP.NET Core. Lax est le reglage correct ici : le
        // cookie de SESSION, lui, reste en Strict.
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = bff.RequireSecureCookie
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.NonceCookie.SecurePolicy = options.CorrelationCookie.SecurePolicy;

        options.TokenValidationParameters.NameClaimType = "preferred_username";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;

        options.Events.OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is ClaimsIdentity identity)
            {
                KeycloakClaimsFlattener.Flatten(identity);
            }

            return Task.CompletedTask;
        };

        options.Events.OnRemoteFailure = context =>
        {
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Forterro.Bff.Oidc");

            logger.LogWarning(context.Failure, "Echec du retour d'authentification.");

            // Sans ce HandleResponse, l'echec remonte en 500 avec une trace complete :
            // un utilisateur qui annule sur l'ecran Keycloak n'est pas une erreur serveur.
            context.HandleResponse();
            context.Response.Redirect("/bff/login-failed");

            return Task.CompletedTask;
        };
    }

    private static Task WriteStatus<TOptions>(RedirectContext<TOptions> context, int statusCode)
        where TOptions : AuthenticationSchemeOptions
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = MediaTypeNames.Application.Json;

        return context.Response.WriteAsync(
            $"{{\"type\":\"about:blank\",\"title\":\"Session absente ou expiree\",\"status\":{statusCode}}}");
    }
}
