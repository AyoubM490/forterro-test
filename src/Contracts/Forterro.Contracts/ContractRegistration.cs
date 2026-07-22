using Forterro.BuildingBlocks.Messaging;

namespace Forterro.Contracts;

public static class ContractRegistration
{
    /// <summary>
    /// Declaration unique de tous les contrats du domaine.
    /// Chaque service appelle cette methode : impossible qu'un producteur et un consommateur
    /// divergent sur le nom de contrat ou sur le topic.
    /// </summary>
    public static IntegrationEventRegistry AddBusinessServicesContracts(this IntegrationEventRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry
            .Register<InvoiceIssued>(ContractNames.InvoiceIssued, Topics.Invoicing)
            .Register<InvoiceCancelled>(ContractNames.InvoiceCancelled, Topics.Invoicing)
            .Register<InvoicePaid>(ContractNames.InvoicePaid, Topics.Invoicing)
            .Register<PaymentSettled>(ContractNames.PaymentSettled, Topics.Payments)
            .Register<PaymentFailed>(ContractNames.PaymentFailed, Topics.Payments);
    }
}
