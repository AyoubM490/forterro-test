using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Distributed;

namespace Forterro.Bff.Authentication;

/// <summary>
/// Garde le ticket d'authentification cote serveur ; le navigateur ne recoit qu'une cle opaque.
///
/// C'est ce qui fait du BFF autre chose qu'un proxy : sans ce store, ASP.NET Core serialise le
/// ticket — jetons d'acces et de rafraichissement compris — dans le cookie lui-meme. Le cookie
/// est chiffre, donc illisible pour le navigateur, mais il pose deux problemes reels :
///
/// 1. Il ne peut pas etre revoque. Un logout efface le cookie du navigateur, mais une copie
///    exfiltree reste valide jusqu'a son expiration. Ici, supprimer l'entree du cache tue la
///    session immediatement, partout.
/// 2. Il grossit. Un jeton Keycloak avec roles et scopes depasse vite 4 Ko a lui seul ; le
///    cookie est alors decoupe en morceaux et certains proxies rejettent l'en-tete.
///
/// En production, l'implementation d'<see cref="IDistributedCache"/> doit etre Redis : avec le
/// cache memoire, deux replicas du BFF ne partagent pas les sessions et l'utilisateur est
/// deconnecte a chaque fois que le load balancer change de pod.
/// </summary>
internal sealed class DistributedTicketStore(
    IDistributedCache cache,
    ILogger<DistributedTicketStore> logger) : ITicketStore
{
    private const string KeyPrefix = "bff:session:";

    private static readonly TicketSerializer Serializer = TicketSerializer.Default;

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        // Guid v4 : 122 bits d'entropie, non devinable. Le prefixe n'est la que pour
        // rendre les cles lisibles dans un redis-cli.
        var key = $"{KeyPrefix}{Guid.NewGuid():N}";
        await RenewAsync(key, ticket);

        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var expiresAt = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(8);

        await cache.SetAsync(
            key,
            Serializer.Serialize(ticket),
            new DistributedCacheEntryOptions { AbsoluteExpiration = expiresAt });
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var payload = await cache.GetAsync(key);

        if (payload is null)
        {
            // Cas normal, pas une anomalie : session expiree, revoquee, ou emise par un
            // BFF dont le cache a redemarre. L'utilisateur repasse par /bff/login.
            logger.LogDebug("Session {Key} absente du store.", key);
            return null;
        }

        return Serializer.Deserialize(payload);
    }

    public Task RemoveAsync(string key) => cache.RemoveAsync(key);
}
