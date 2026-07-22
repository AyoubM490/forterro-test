namespace Forterro.OpenBanking.Api.Bank;

/// <summary>
/// Statuts de paiement ISO 20022, tels que remontes par les API PSD2.
/// On garde la nomenclature de la norme plutot qu'un enum maison : les equipes
/// bancaires et les logs partenaires parlent ce vocabulaire.
/// </summary>
public enum PaymentStatus
{
    /// <summary>Received — l'ordre est accepte par la banque, pas encore execute.</summary>
    Received,

    /// <summary>PendingSca — en attente d'authentification forte du client (PSD2 art. 97).</summary>
    PendingSca,

    /// <summary>AcceptedSettlementInProcess — execution en cours.</summary>
    InProgress,

    /// <summary>AcceptedSettlementCompleted — fonds transferes.</summary>
    Settled,

    /// <summary>Rejected — refus definitif.</summary>
    Rejected,
}

/// <summary>Ordre de virement SEPA (PSD2 Payment Initiation Service).</summary>
public sealed record PaymentInitiation
{
    public required string DebtorIban { get; init; }

    public required string CreditorIban { get; init; }

    public required string CreditorName { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>
    /// Reference de bout en bout, transmise telle quelle par la banque.
    /// C'est elle qui permet le rapprochement automatique avec la facture.
    /// </summary>
    public required string EndToEndId { get; init; }

    /// <summary>Libelle porte sur le releve du beneficiaire (140 caracteres max, norme SEPA).</summary>
    public string? RemittanceInformation { get; init; }
}

public sealed record PaymentResult(
    string BankPaymentId,
    PaymentStatus Status,
    string? BankReference,
    DateTimeOffset? SettledAt,
    string? RejectionCode,
    string? RejectionReason);

public sealed record AccountBalance(
    string Iban,
    decimal Available,
    decimal Booked,
    string Currency,
    DateTimeOffset AsOf);

/// <summary>
/// Erreur remontee par la banque, avec l'information cle : est-ce rejouable ?
/// Un timeout se rejoue, un "compte cloture" jamais.
/// </summary>
public sealed class BankException(string code, string message, bool isRetryable)
    : Exception(message)
{
    public string Code { get; } = code;

    public bool IsRetryable { get; } = isRetryable;
}
