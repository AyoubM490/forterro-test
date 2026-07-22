using Forterro.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Forterro.BuildingBlocks.Persistence;

/// <summary>Trace d'un evenement deja consomme. Cle primaire = EventId, la dedup est donc gratuite.</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }

    public string ContractName { get; set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; set; }
}

public interface IInboxDbContext
{
    DbSet<ProcessedEvent> ProcessedEvents { get; }
}

public sealed class EfProcessedEventStore<TContext>(TContext context) : IProcessedEventStore
    where TContext : DbContext, IInboxDbContext
{
    public Task<bool> HasBeenProcessedAsync(Guid eventId, CancellationToken cancellationToken)
        => context.ProcessedEvents.AsNoTracking().AnyAsync(e => e.EventId == eventId, cancellationToken);

    public async Task MarkAsProcessedAsync(
        Guid eventId,
        string contractName,
        CancellationToken cancellationToken)
    {
        context.ProcessedEvents.Add(new ProcessedEvent
        {
            EventId = eventId,
            ContractName = contractName,
            ProcessedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(cancellationToken);
    }
}
