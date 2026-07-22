using Forterro.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Forterro.BuildingBlocks.Outbox;

/// <summary>
/// Ecriture d'un evenement dans l'Outbox. Appele DEPUIS le handler metier,
/// avant le SaveChanges : l'evenement et l'effet metier partagent la transaction.
/// </summary>
public interface IOutboxWriter
{
    void Enqueue(IntegrationEvent @event);
}

/// <summary>
/// Implemente par le DbContext de chaque service. C'est le seul couplage impose
/// par la brique Outbox : la table vit dans la base du service, jamais ailleurs.
/// </summary>
public interface IOutboxDbContext
{
    DbSet<OutboxMessage> OutboxMessages { get; }
}
