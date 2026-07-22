using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Forterro.BuildingBlocks.Api;

/// <summary>
/// Rend le corps des requetes mutantes relisable.
///
/// Pourquoi c'est necessaire : dans les Minimal APIs, un endpoint filter s'execute
/// APRES la liaison des parametres. A ce moment le flux de requete est deja consomme
/// et non rembobinable, donc <see cref="IdempotencyFilter"/> calculerait l'empreinte
/// d'un corps vide — et considererait deux requetes differentes comme identiques.
///
/// On active donc la mise en tampon en amont du routage, uniquement sur les verbes
/// porteurs de corps : inutile de payer ce cout sur les GET.
/// </summary>
public sealed class RequestBufferingMiddleware(RequestDelegate next)
{
    /// <summary>Au-dela, le tampon bascule sur disque plutot que de saturer la memoire.</summary>
    private const int MemoryThresholdBytes = 64 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method))
        {
            context.Request.EnableBuffering(MemoryThresholdBytes);
        }

        await next(context);
    }
}

public static class RequestBufferingMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestBuffering(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<RequestBufferingMiddleware>();
    }
}
