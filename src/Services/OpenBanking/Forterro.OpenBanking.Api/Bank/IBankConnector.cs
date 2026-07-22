namespace Forterro.OpenBanking.Api.Bank;

/// <summary>
/// Couche anti-corruption devant les API bancaires.
///
/// Chaque banque implemente PSD2 a sa maniere (Berlin Group, STET, dialectes maison).
/// Ce contrat isole le reste du systeme de ces differences : ajouter une banque,
/// c'est ecrire une implementation, pas modifier la saga de paiement.
/// </summary>
public interface IBankConnector
{
    /// <summary>
    /// Initie un virement.
    /// <paramref name="idempotencyKey"/> est obligatoire : c'est la seule garantie
    /// qu'un retry reseau ne produit pas un second debit.
    /// </summary>
    Task<PaymentResult> InitiatePaymentAsync(
        PaymentInitiation initiation,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<PaymentResult> GetPaymentStatusAsync(string bankPaymentId, CancellationToken cancellationToken);

    Task<AccountBalance> GetBalanceAsync(string iban, CancellationToken cancellationToken);
}
