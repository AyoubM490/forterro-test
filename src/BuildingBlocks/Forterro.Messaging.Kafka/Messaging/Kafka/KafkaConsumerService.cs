using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Forterro.BuildingBlocks.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Forterro.BuildingBlocks.Messaging.Kafka;

/// <summary>
/// Boucle de consommation Kafka.
///
/// Choix structurants :
///  - commit MANUEL de l'offset, apres traitement reussi (at-least-once assume) ;
///  - deduplication via <see cref="IProcessedEventStore"/> (inbox) ;
///  - retry en memoire avec backoff exponentiel, puis dead-letter topic ;
///  - un scope DI par message (comme une requete HTTP) : le DbContext est bien isole.
/// </summary>
public sealed class KafkaConsumerService(
    IOptions<KafkaOptions> options,
    IntegrationEventRegistry registry,
    IServiceScopeFactory scopeFactory,
    IEventPublisher publisher,
    ILogger<KafkaConsumerService> logger) : BackgroundService
{
    private readonly KafkaOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Le broker n'est pas forcement pret au demarrage du pod : on sort du chemin critique.
        await Task.Yield();

        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            SessionTimeoutMs = 45_000,
            MaxPollIntervalMs = 300_000,
        };
        KafkaEventPublisher.ApplySecurity(config, _options);

        using var consumer = new ConsumerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, e) => logger.LogError("Erreur Kafka : {Reason}", e.Reason))
            .SetPartitionsAssignedHandler((_, parts) =>
                logger.LogInformation("Partitions assignees : {Partitions}", string.Join(", ", parts)))
            .Build();

        var topics = _options.Topics.Count > 0 ? _options.Topics : [.. registry.AllTopics];
        consumer.Subscribe(topics);
        logger.LogInformation(
            "Consommateur {Group} abonne a {Topics}", _options.ConsumerGroupId, string.Join(", ", topics));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    logger.LogError(ex, "Echec de consommation, poursuite de la boucle.");
                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                await ProcessWithRetryAsync(result, stoppingToken);

                // On ne commite QU'APRES traitement (ou mise en DLQ) : pas de perte silencieuse.
                consumer.StoreOffset(result);
                consumer.Commit(result);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Arret propre du consommateur {Group}.", _options.ConsumerGroupId);
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task ProcessWithRetryAsync(
        ConsumeResult<string, byte[]> result,
        CancellationToken cancellationToken)
    {
        var contractName = ReadHeader(result.Message.Headers, KafkaEventPublisher.ContractHeader);
        var payload = Encoding.UTF8.GetString(result.Message.Value);

        if (contractName is null || !registry.TryResolve(contractName, out var eventType))
        {
            // Contrat inconnu : ce n'est pas une erreur, un autre service peut etre le destinataire.
            logger.LogDebug("Contrat {Contract} ignore (non enregistre ici).", contractName);
            return;
        }

        for (var attempt = 1; attempt <= _options.MaxDeliveryAttempts; attempt++)
        {
            try
            {
                await DispatchAsync(eventType, contractName, payload, result, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == _options.MaxDeliveryAttempts)
                {
                    logger.LogError(
                        ex,
                        "Contrat {Contract} en echec definitif apres {Attempts} tentatives -> dead-letter.",
                        contractName, attempt);

                    await SendToDeadLetterAsync(result, contractName, payload, ex, cancellationToken);
                    return;
                }

                var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
                logger.LogWarning(
                    ex,
                    "Tentative {Attempt}/{Max} en echec pour {Contract}, nouvelle tentative dans {Delay}ms.",
                    attempt, _options.MaxDeliveryAttempts, contractName, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task DispatchAsync(
        Type eventType,
        string contractName,
        string payload,
        ConsumeResult<string, byte[]> result,
        CancellationToken cancellationToken)
    {
        using var activity = Telemetry.ActivitySource.StartActivity(
            $"{result.Topic} process",
            ActivityKind.Consumer,
            parentId: ReadHeader(result.Message.Headers, KafkaEventPublisher.TraceParentHeader));

        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.source.name", result.Topic);
        activity?.SetTag("forterro.contract", contractName);

        if (JsonSerializer.Deserialize(payload, eventType, MessagingJson.Options)
            is not IntegrationEvent @event)
        {
            throw new InvalidOperationException(
                $"Payload illisible pour le contrat {contractName}.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var inbox = sp.GetService<IProcessedEventStore>();
        if (inbox is not null && await inbox.HasBeenProcessedAsync(@event.EventId, cancellationToken))
        {
            logger.LogDebug("Evenement {EventId} deja traite, ignore (inbox).", @event.EventId);
            return;
        }

        var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
        var handlers = sp.GetServices(handlerType).Where(h => h is not null).ToList();

        if (handlers.Count == 0)
        {
            logger.LogDebug("Aucun handler pour {Contract} dans ce service.", contractName);
            return;
        }

        var method = handlerType.GetMethod(nameof(IIntegrationEventHandler<IntegrationEvent>.HandleAsync))!;
        foreach (var handler in handlers)
        {
            await (Task)method.Invoke(handler, [@event, cancellationToken])!;
        }

        if (inbox is not null)
        {
            await inbox.MarkAsProcessedAsync(@event.EventId, contractName, cancellationToken);
        }
    }

    private async Task SendToDeadLetterAsync(
        ConsumeResult<string, byte[]> result,
        string contractName,
        string payload,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishRawAsync(
                result.Topic + _options.DeadLetterTopicSuffix,
                contractName,
                result.Message.Key ?? string.Empty,
                payload,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["x-dlq-reason"] = exception.Message,
                    ["x-dlq-source-topic"] = result.Topic,
                    ["x-dlq-source-offset"] = result.Offset.Value.ToString(CultureInfo.InvariantCulture),
                },
                // Mise en DLQ : on est dans l'activite de consommation, qui porte deja
                // le contexte du message d'origine.
                parentTraceParent: null,
                cancellationToken: cancellationToken);
        }
        catch (Exception dlqEx)
        {
            // Perdre le message serait pire que bruyant : on le trace explicitement.
            logger.LogCritical(
                dlqEx,
                "Impossible d'ecrire en dead-letter. Message PERDU : topic={Topic} offset={Offset} payload={Payload}",
                result.Topic, result.Offset.Value, payload);
        }
    }

    private static string? ReadHeader(Headers? headers, string key)
    {
        if (headers is not null && headers.TryGetLastBytes(key, out var bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return null;
    }
}
