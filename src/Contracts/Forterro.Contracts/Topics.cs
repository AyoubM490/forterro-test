namespace Forterro.Contracts;

/// <summary>
/// Topics Kafka. Le numero de version fait partie du nom : une rupture de contrat
/// se materialise par un nouveau topic (.v2) que les consommateurs adoptent a leur rythme,
/// jamais par une modification en place d'un topic en production.
/// </summary>
public static class Topics
{
    public const string Invoicing = "forterro.invoicing.v1";
    public const string Payments = "forterro.payments.v1";
    public const string OpenBanking = "forterro.openbanking.v1";
}

/// <summary>
/// Noms logiques des contrats, transportes dans le header <c>x-contract-name</c>.
/// Decoupler ce nom du nom de classe .NET permet de refactorer le code sans
/// casser les consommateurs deployes.
/// </summary>
public static class ContractNames
{
    public const string InvoiceIssued = "invoicing.invoice-issued.v1";
    public const string InvoiceCancelled = "invoicing.invoice-cancelled.v1";
    public const string InvoicePaid = "invoicing.invoice-paid.v1";

    public const string PaymentSettled = "payments.payment-settled.v1";
    public const string PaymentFailed = "payments.payment-failed.v1";
}
