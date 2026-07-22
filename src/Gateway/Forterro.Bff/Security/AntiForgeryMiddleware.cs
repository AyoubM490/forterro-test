using Forterro.Bff.Authentication;
using Microsoft.Extensions.Options;

namespace Forterro.Bff.Security;

/// <summary>
/// Protection CSRF du chemin navigateur.
///
/// Le probleme, en une phrase : le cookie de session est AMBIANT. Le navigateur l'attache tout
/// seul a une requete vers le BFF, meme si cette requete a ete declenchee par un formulaire
/// heberge sur un site tiers. Sans garde, evil.example peut faire emettre une facture au nom
/// d'un utilisateur simplement connecte dans un autre onglet.
///
/// La defense repose sur un en-tete applicatif que le navigateur refuse d'envoyer en
/// cross-origin sans autorisation prealable : toute requete portant un en-tete personnalise
/// declenche un preflight OPTIONS, que le BFF ne valide pour aucune origine tierce. Un
/// formulaire HTML, lui, ne peut poser aucun en-tete. L'en-tete est donc une preuve que la
/// requete vient de code que nous avons servi.
///
/// S'y ajoute le controle d'<c>Origin</c>, et en amont <c>SameSite=Strict</c> sur le cookie.
/// Trois barrieres independantes, parce qu'aucune n'est parfaite seule : SameSite depend du
/// navigateur, et Origin est absent de certaines requetes anciennes.
///
/// Ne s'applique JAMAIS au chemin machine : un jeton porteur n'est pas ambiant, l'appelant doit
/// le poser explicitement. Exiger un en-tete anti-CSRF d'un worker serait de la ceremonie.
/// </summary>
internal sealed class AntiForgeryMiddleware(RequestDelegate next, IOptions<BffOptions> options)
{
    public const string HeaderName = "X-Forterro-Csrf";

    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS", "TRACE"];

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (RequiresProtection(context) && !IsTrustworthy(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://forterro.dev/problems/csrf",
                title = "Requete rejetee",
                status = StatusCodes.Status403Forbidden,
                detail = $"L'en-tete {HeaderName} est obligatoire sur les requetes de session.",
            });

            return;
        }

        await next(context);
    }

    private static bool RequiresProtection(HttpContext context)
    {
        if (SafeMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // Meme critere que la selection de schema, volontairement : voir IsMachineRequest.
        return !BffAuthentication.IsMachineRequest(context.Request);
    }

    private bool IsTrustworthy(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey(HeaderName))
        {
            return false;
        }

        var origin = context.Request.Headers.Origin.ToString();

        if (string.IsNullOrEmpty(origin))
        {
            // Absent sur les navigations de premier niveau anciennes. L'en-tete personnalise
            // a deja fait son office ; on ne durcit pas au point de casser des clients valides.
            return true;
        }

        return options.Value.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
            || string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}
