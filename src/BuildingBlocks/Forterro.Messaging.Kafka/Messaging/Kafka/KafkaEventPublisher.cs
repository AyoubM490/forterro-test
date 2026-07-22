using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Forterro.BuildingBlocks.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Forterro.BuildingBlocks.Messaging.Kafka;

/// <summary>
/// Producteur Kafka idempotent. Le contexte de trace (W3C traceparent) est propage
/// dans les headers : une trace distribuee traverse HTTP -> Outbox -> Kafka -> consommateur.
/// </summary>
public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    // Les valeurs vivent desormais dans MessagingHeaders, cote abstractions : ce sont
    // des noms d'en-tete neutres du transport, et l'Outbox en a besoin sans devoir
    // referencer Kafka. Ces alias restent pour ne casser aucun appelant existant.
    public const string ContractHeader = MessagingHeaders.ContractName;
    public const string EventIdHeader = MessagingHeaders.EventId;
    public const string TraceParentHeader = MessagingHeaders.TraceParent;
    public const string CorrelationHeader = MessagingHeaders.Correlation;

    private readonly IProducer<string, byte[]> _producer;
    private readonly IntegrationEventRegistry _registry;
    private readonly ILogger<KafkaEventPublisher> _logger;

    public KafkaEventPublisher(
        IOptions<KafkaOptions> options,
        IntegrationEventRegistry registry,
        ILogger<KafkaEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _registry = registry;
        _logger = logger;

        var o = options.Value;
        var config = new ProducerConfig
        {
            BootstrapServers = o.BootstrapServers,
            EnableIdempotence = o.EnableIdempotence,
            Acks = Acks.All,
            MessageSendMaxRetries = 10,
            RetryBackoffMs = 200,
            // Requis pour garantir l'ordre avec l'idempotence activee.
            MaxInFlight = 5,
            CompressionType = CompressionType.Snappy,
            LingerMs = 5,
        };

        ApplySecurity(config, o);
        _producer = new ProducerBuilder<string, byte[]>(config).Build();
    }

    internal static void ApplySecurity(ClientConfig config, KafkaOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.SecurityProtocol))
        {
            return;
        }

        config.SecurityProtocol = Enum.Parse<SecurityProtocol>(o.SecurityProtocol, ignoreCase: true);

        if (!string.IsNullOrWhiteSpace(o.SaslMechanism))
        {
            config.SaslMechanism = Enum.Parse<SaslMechanism>(o.SaslMechanism, ignoreCase: true);
            config.SaslUsername = o.SaslUsername;
            config.SaslPassword = o.SaslPassword;
        }
    }

    public Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var type = @event.GetType();
        var payload = JsonSerializer.Serialize(@event, type, MessagingJson.Options);

        return PublishRawAsync(
            _registry.GetTopic(type),
            _registry.GetContractName(type),
            @event.PartitionKey,
            payload,
            headers: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventIdHeader] = @event.EventId.ToString(),
            },
            // Publication directe : on est deja dans le contexte de l'appelant,
            // Activity.Current suffit a rattacher la trace.
            parentTraceParent: null,
            cancellationToken: cancellationToken);
    }

    public async Task PublishRawAsync(
        string topic,
        string contractName,
        string partitionKey,
        string payloadJson,
        IReadOnlyDictionary<string, string>? headers = null,
        string? parentTraceParent = null,
        CancellationToken cancellationToken = default)
    {
        // Rattachement explicite au contexte de l'operation d'origine quand il est fourni.
        //
        // Depuis l'Outbox c'est la SEULE facon de conserver la trace : le dispatcher tourne
        // dans un BackgroundService, donc Activity.Current y est nulle ou sans rapport avec
        // la requete qui a ecrit l'evenement. En repartant du traceparent persiste, la trace
        // de l'appel HTTP se prolonge dans la publication Kafka puis chez le consommateur.
        using var activity = string.IsNullOrEmpty(parentTraceParent)
            ? Telemetry.ActivitySource.StartActivity($"{topic} publish", ActivityKind.Producer)
            : Telemetry.ActivitySource.StartActivity(
                $"{topic} publish", ActivityKind.Producer, parentId: parentTraceParent);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", topic);
        activity?.SetTag("forterro.contract", contractName);

        var kafkaHeaders = new Headers
        {
            { ContractHeader, Encoding.UTF8.GetBytes(contractName) },
        };

        var traceParent = activity?.Id ?? Activity.Current?.Id;
        if (!string.IsNullOrEmpty(traceParent))
        {
            kafkaHeaders.Add(TraceParentHeader, Encoding.UTF8.GetBytes(traceParent));
        }

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                kafkaHeaders.Add(key, Encoding.UTF8.GetBytes(value));
            }
        }

        var message = new Message<string, byte[]>
        {
            Key = partitionKey,
            Value = Encoding.UTF8.GetBytes(payloadJson),
            Headers = kafkaHeaders,
        };

        var result = await _producer.ProduceAsync(topic, message, cancellationToken);

        _logger.LogInformation(
            "Evenement {Contract} publie sur {Topic}[{Partition}]@{Offset}",
            contractName, topic, result.Partition.Value, result.Offset.Value);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}
