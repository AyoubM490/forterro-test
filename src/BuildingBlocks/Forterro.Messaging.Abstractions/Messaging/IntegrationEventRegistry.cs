using System.Diagnostics.CodeAnalysis;

namespace Forterro.BuildingBlocks.Messaging;

/// <summary>
/// Table de correspondance "nom logique du contrat" -> type CLR + topic.
/// On ne serialise JAMAIS le nom de type .NET dans le message : un renommage de classe
/// casserait tous les consommateurs. Le nom logique est le contrat public.
/// </summary>
public sealed class IntegrationEventRegistry
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];
    private readonly Dictionary<Type, string> _topics = [];

    public IntegrationEventRegistry Register<TEvent>(string contractName, string topic)
        where TEvent : IntegrationEvent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        _byName[contractName] = typeof(TEvent);
        _byType[typeof(TEvent)] = contractName;
        _topics[typeof(TEvent)] = topic;
        return this;
    }

    public bool TryResolve(string contractName, [NotNullWhen(true)] out Type? eventType)
        => _byName.TryGetValue(contractName, out eventType);

    public string GetContractName(Type eventType)
        => _byType.TryGetValue(eventType, out var name)
            ? name
            : throw new InvalidOperationException(
                $"Le type {eventType.Name} n'est pas enregistre dans l'IntegrationEventRegistry.");

    public string GetTopic(Type eventType)
        => _topics.TryGetValue(eventType, out var topic)
            ? topic
            : throw new InvalidOperationException(
                $"Aucun topic declare pour {eventType.Name}.");

    public IReadOnlyCollection<string> AllTopics => [.. _topics.Values.Distinct(StringComparer.Ordinal)];
}
