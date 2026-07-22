using Forterro.BuildingBlocks.Messaging;

namespace Forterro.Contracts;

/// <summary>Le virement a ete execute et confirme par la banque.</summary>
public sealed record PaymentSettled : IntegrationEvent
{
    public required Guid PaymentId { get; init; }

    public required Guid InvoiceId { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>Reference de la banque (end-to-end id PSD2). Piste d'audit.</summary>
    public required string BankReference { get; init; }

    public required DateTimeOffset SettledAt { get; init; }

    // Partitionne sur la facture, pas sur le paiement : garantit que tous les evenements
    // relatifs a une meme facture arrivent dans l'ordre sur la meme partition.
    public override string PartitionKey => InvoiceId.ToString();
}

/// <summary>
/// Le paiement a echoue de maniere definitive (apres retries et compensation).
/// La saga a deja annule ce qu'elle devait annuler avant de publier ceci.
/// </summary>
public sealed record PaymentFailed : IntegrationEvent
{
    public required Guid PaymentId { get; init; }

    public required Guid InvoiceId { get; init; }

    /// <summary>Code stable, exploitable par les consommateurs (pas un message libre).</summary>
    public required string FailureCode { get; init; }

    public required string Reason { get; init; }

    public required bool IsRetryable { get; init; }

    public override string PartitionKey => InvoiceId.ToString();
}

public static class PaymentFailureCodes
{
    public const string InsufficientFunds = "insufficient_funds";
    public const string InvalidIban = "invalid_iban";
    public const string BankUnavailable = "bank_unavailable";
    public const string Rejected = "rejected_by_bank";
    public const string Timeout = "timeout";
}
