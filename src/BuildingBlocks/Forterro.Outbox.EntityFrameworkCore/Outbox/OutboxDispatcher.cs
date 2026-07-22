using Forterro.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Forterro.BuildingBlocks.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 50;

    /// <summary>Duree du bail pose sur un lot. Doit couvrir le temps de publication.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxAttempts { get; set; } = 10;

    /// <summary>Retention des messages publies, pour l'audit et le rejeu.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Relais Outbox -> Kafka.
///
/// Tourne dans chaque replica du service. La concurrence est geree par un bail
/// (<c>LeasedUntil</c>) protege par un jeton de version : si deux replicas tentent
/// de prendre le meme lot, l'un des deux se prend un <see cref="DbUpdateConcurrencyException"/>
/// et repasse au tour suivant. Pas de "leader election" a maintenir.
/// </summary>
public sealed class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher<TContext>> logger) : BackgroundService
    where TContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = options.Value;
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await DispatchBatchAsync(stoppingToken);

                // Lot plein : il reste probablement du travail, on enchaine sans attendre.
                if (published >= _options.BatchSize)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cycle de dispatch Outbox en echec, nouvelle tentative.");
            }

            await Task.Delay(_options.PollingInterval, stoppingToken);
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var now = DateTimeOffset.UtcNow;

        var batch = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null
                        && m.Attempts < _options.MaxAttempts
                        && (m.LeasedUntil == null || m.LeasedUntil < now))
            .OrderBy(m => m.OccurredAt)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            return 0;
        }

        foreach (var message in batch)
        {
            message.LeasedUntil = now.Add(_options.LeaseDuration);
            message.LeasedBy = _instanceId;
            message.Version++;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Un autre replica a pris le lot. Comportement nominal, pas une erreur.
            logger.LogDebug("Lot Outbox deja pris par une autre instance, on passe.");
            return 0;
        }

        var published = 0;
        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishRawAsync(
                    message.Topic,
                    message.ContractName,
                    message.PartitionKey,
                    message.Payload,
                    BuildHeaders(message),
                    // Le contexte de trace capture a l'ECRITURE, pas celui de cette boucle
                    // de fond : c'est ce qui relie la saga a l'appel HTTP d'origine.
                    parentTraceParent: message.TraceParent,
                    cancellationToken: cancellationToken);

                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message;
                message.LeasedUntil = null;
                message.LeasedBy = null;

                logger.LogError(
                    ex,
                    "Publication de {MessageId} ({Contract}) en echec, tentative {Attempts}/{Max}.",
                    message.Id, message.ContractName, message.Attempts, _options.MaxAttempts);
            }

            message.Version++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return published;
    }

    private static Dictionary<string, string> BuildHeaders(OutboxMessage message)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagingHeaders.EventId] = message.Id.ToString(),
        };

        if (!string.IsNullOrEmpty(message.TraceParent))
        {
            headers[MessagingHeaders.Correlation] = message.TraceParent;
        }

        return headers;
    }
}
