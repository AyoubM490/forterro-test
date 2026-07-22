using System.Net.Http.Headers;
using Forterro.Bff.Authentication;
using Microsoft.AspNetCore.Authentication;

namespace Forterro.Bff.Proxy;

/// <summary>
/// Determine quel jeton porter vers les services en aval.
///
/// Le BFF ne fabrique pas d'identite : il transmet celle de l'appelant, avec ses scopes. Les
/// services metier revalident jeton, audience et scopes de leur cote — le BFF n'est pas un
/// point de confiance unique, et un attaquant qui le contournerait pour atteindre directement
/// un service se heurterait aux memes controles. C'est la difference entre defense en
/// profondeur et simple perimetre.
///
/// Ce qu'on ne fait PAS ici : elargir les droits. Il serait techniquement simple de troquer le
/// jeton de l'appelant contre un jeton de service tout-puissant ; ce serait aussi le moyen le
/// plus court de transformer chaque endpoint du BFF en escalade de privileges.
/// </summary>
internal static class OutboundToken
{
    public static async Task<AuthenticationHeaderValue?> ResolveAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Chemin machine : l'en-tete d'origine est deja le bon jeton, avec les scopes de la
        // ligne produit appelante. On le transmet tel quel.
        if (BffAuthentication.IsMachineRequest(context.Request))
        {
            return AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var incoming)
                ? incoming
                : null;
        }

        // Chemin navigateur : le jeton sort de la session serveur, jamais du client.
        var accessToken = await context.GetTokenAsync(BffAuthentication.SessionScheme, "access_token");

        return accessToken is null ? null : new AuthenticationHeaderValue("Bearer", accessToken);
    }
}
