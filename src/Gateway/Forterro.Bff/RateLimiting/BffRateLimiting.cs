using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Forterro.Bff.RateLimiting;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Requetes par fenetre pour une ligne produit (appel machine).</summary>
    public int MachinePermitLimit { get; set; } = 600;

    /// <summary>Requetes par fenetre pour une session navigateur. Un humain clique moins vite.</summary>
    public int SessionPermitLimit { get; set; } = 120;

    /// <summary>Requetes par fenetre pour un appelant non authentifie, par adresse IP.</summary>
    public int AnonymousPermitLimit { get; set; } = 30;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Limitation de debit a la porte d'entree.
///
/// Le decoupage se fait par APPELANT, pas globalement : une limite globale transforme
/// n'importe quel client trop bavard en panne generale pour tous les autres. Ici, une ligne
/// produit qui part en boucle epuise son propre quota et personne d'autre ne le remarque.
/// </summary>
internal static class BffRateLimiting
{
    public static IServiceCollection AddBffRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName));

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Resolu par requete, jamais capture a l'enregistrement : une valeur lue trop
                // tot fige la configuration presente a cet instant et ignore les sources
                // ajoutees ensuite. Ici, cela ferait tourner la production avec les quotas
                // par defaut sans qu'aucune erreur ne le signale.
                var limits = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                var (partition, permits) = Classify(context, limits);

                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = limits.Window,
                    // File d'attente a zero : on refuse tout de suite plutot que de retenir la
                    // requete. Un appelant qui attend derriere une file finit en timeout et
                    // rejoue, ce qui aggrave la charge exactement quand il ne faut pas.
                    QueueLimit = 0,
                });
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                var limits = context.HttpContext.RequestServices
                    .GetRequiredService<IOptions<RateLimitOptions>>().Value;

                // Retry-After transforme un refus en information exploitable : sans lui, un
                // client bien ecrit n'a aucun moyen de savoir quand reessayer et boucle.
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)limits.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        type = "https://forterro.dev/problems/rate-limit",
                        title = "Trop de requetes",
                        status = StatusCodes.Status429TooManyRequests,
                    },
                    cancellationToken);
            };
        });

        return services;
    }

    private static (string Partition, int Permits) Classify(HttpContext context, RateLimitOptions limits)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "inconnu";
            return ($"anon:{ip}", limits.AnonymousPermitLimit);
        }

        // azp (authorized party) identifie le CLIENT OAuth, pas l'utilisateur. C'est la bonne
        // maille pour une ligne produit : son service account a un sub unique, mais c'est le
        // client qu'on veut limiter, meme s'il obtient plusieurs jetons.
        var clientId = context.User.FindFirstValue("azp") ?? context.User.FindFirstValue("client_id");
        var subject = context.User.FindFirstValue("sub");

        return Authentication.BffAuthentication.IsMachineRequest(context.Request)
            ? ($"machine:{clientId ?? subject}", limits.MachinePermitLimit)
            : ($"session:{subject}", limits.SessionPermitLimit);
    }
}
