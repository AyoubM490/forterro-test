using System.Diagnostics;
using System.Text.Json;
using Forterro.BuildingBlocks.Messaging;

namespace Forterro.BuildingBlocks.Outbox;

public sealed class OutboxWriter<TContext>(TContext context, IntegrationEventRegistry registry) : IOutboxWriter
    where TContext : IOutboxDbContext
{
    public void Enqueue(IntegrationEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var type = @event.GetType();

        context.OutboxMessages.Add(new OutboxMessage
        {
            Id = @event.EventId,
            ContractName = registry.GetContractName(type),
            Topic = registry.GetTopic(type),
            PartitionKey = @event.PartitionKey,
            Payload = JsonSerializer.Serialize(@event, type, MessagingJson.Options),
            OccurredAt = @event.OccurredAt,
            TraceParent = Activity.Current?.Id,
        });
    }
}
