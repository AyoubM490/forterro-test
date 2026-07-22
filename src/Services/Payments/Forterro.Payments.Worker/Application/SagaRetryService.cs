using Forterro.Payments.Worker.Domain;
using Forterro.Payments.Worker.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Forterro.Payments.Worker.Application;

/// <summary>
/// Planificateur de reprise des sagas.
///
/// Sans lui, une saga tombee sur une banque indisponible resterait bloquee : Kafka a
/// deja commite l'offset, l'evenement ne reviendra jamais. C'est le point que l'on oublie
/// le plus souvent en passant a l'asynchrone — le broker ne rejoue pas la logique metier,
/// il ne fait que livrer des messages.
///
/// Concurrence entre replicas : le verrou est pose par un UPDATE conditionnel
/// (concurrence optimiste sur xmin), pas par un verrou applicatif.
/// </summary>
public sealed class SagaRetryService(
    IServiceScopeFactory scopeFactory,
    ILogger<SagaRetryService> logger) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Au-dela de ce delai, une saga encore en <see cref="SagaState.AwaitingBank"/>
    /// est consideree comme orpheline : le worker a ete tue pendant l'appel sortant,
    /// donc aucun gestionnaire d'exception n'a pu s'executer. Sans cette recuperation,
    /// elle resterait bloquee pour toujours (Kafka a deja commite son offset).
    /// </summary>
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(2);

    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(PollingInterval);

        do
        {
            try
            {
                await ProcessDueSagasAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Cycle de reprise des sagas en echec.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task ProcessDueSagasAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<PaymentSagaOrchestrator>();

        var now = DateTimeOffset.UtcNow;
        var stuckBefore = now - StuckThreshold;

        // Deux populations : les reprises planifiees, et les sagas orphelines.
        // La reprise d'une orpheline est sure parce que la cle d'idempotence
        // envoyee a la banque est stable sur toute la duree de vie de la saga.
        var due = await context.Sagas
            .Where(s =>
                (s.State == SagaState.AwaitingRetry && s.NextAttemptAt != null && s.NextAttemptAt <= now)
                || (s.State == SagaState.AwaitingBank && s.UpdatedAt < stuckBefore))
            .OrderBy(s => s.UpdatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return;
        }

        logger.LogInformation("{Count} saga(s) a reprendre.", due.Count);

        foreach (var saga in due)
        {
            try
            {
                await orchestrator.AdvanceAsync(saga, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Un autre replica a repris cette saga entre-temps. Comportement attendu.
                logger.LogDebug("Saga {SagaId} deja reprise ailleurs.", saga.Id);
                context.ChangeTracker.Clear();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reprise de la saga {SagaId} en echec.", saga.Id);
                context.ChangeTracker.Clear();
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
