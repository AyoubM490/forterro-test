using System.ComponentModel.DataAnnotations;

namespace Forterro.Bff.Authentication;

/// <summary>
/// Configuration du chemin navigateur : le BFF est lui-meme un client OAuth confidentiel.
///
/// A ne pas confondre avec la section <c>Oidc</c>, qui decrit comment le BFF *valide* les
/// jetons entrants (role de serveur de ressources). Ici on decrit comment il en *obtient*
/// (role de client). Les deux coexistent et ne portent pas les memes secrets.
/// </summary>
public sealed class BffOptions
{
    public const string SectionName = "Bff";

    /// <summary>
    /// URL du realm, cote reseau interne : c'est de la que le document de decouverte est lu.
    ///
    /// Les URL de redirection du navigateur (authorization_endpoint, end_session_endpoint) ne
    /// sont PAS derivees d'ici mais lues dans ce document. Le serveur d'autorisation doit donc
    /// annoncer une adresse frontale joignable depuis le poste de l'utilisateur — sur Keycloak,
    /// <c>KC_HOSTNAME</c> avec <c>KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true</c>. Sans cela, le login
    /// redirige le navigateur vers <c>http://keycloak:8080</c>, qui n'existe que dans le reseau
    /// Docker, et l'utilisateur voit une page blanche sans le moindre log cote serveur.
    /// </summary>
    [Required]
    public string Authority { get; set; } = string.Empty;

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Scopes demandes au nom de l'utilisateur, en plus de openid/profile/email.</summary>
    public IList<string> Scopes { get; } = [];

    /// <summary>
    /// Origines autorisees a appeler le BFF avec le cookie de session.
    /// Vide = aucune requete cross-origin acceptee, ce qui est le bon defaut.
    /// </summary>
    public IList<string> AllowedOrigins { get; } = [];

    /// <summary>Duree de vie de la session serveur. Glissante : prolongee a chaque requete.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Marge de renouvellement du jeton d'acces. On rafraichit avant l'expiration reelle,
    /// sinon une requete partie juste avant l'echeance arrive expiree en aval.
    /// </summary>
    public TimeSpan RefreshThreshold { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Faux uniquement en developpement local sur http. Un cookie <c>Secure</c> n'est jamais
    /// renvoye par le navigateur sur http://, ce qui produit une boucle de login infinie
    /// impossible a diagnostiquer depuis les logs serveur.
    /// </summary>
    public bool RequireSecureCookie { get; set; } = true;

    /// <summary>Ou renvoyer le navigateur apres login/logout si aucun retour n'est fourni.</summary>
    public string DefaultReturnPath { get; set; } = "/";
}
