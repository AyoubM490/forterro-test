using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Forterro.BuildingBlocks.Outbox;

/// <summary>
/// Purge des messages publies au-dela de la retention.
/// Sans ca, la table Outbox devient le plus gros objet de la base en quelques mois
/// et degrade la requete de dispatch (qui la scanne a chaque cycle).
/// </summary>
public sealed class OutboxCleanupService<TContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxCleanupService<TContext>> logger) : BackgroundService
    where TContext : DbContext, IOutboxDbContext
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();

                var threshold = DateTimeOffset.UtcNow - options.Value.Retention;
                var deleted = await db.OutboxMessages
                    .Where(m => m.ProcessedAt != null && m.ProcessedAt < threshold)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deleted > 0)
                {
                    logger.LogInformation("{Count} messages Outbox purges.", deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Purge de l'Outbox en echec.");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
