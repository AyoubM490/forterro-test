using Forterro.BuildingBlocks.Messaging;
using Forterro.BuildingBlocks.Messaging.Kafka;
using Forterro.BuildingBlocks.Outbox;
using Forterro.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Forterro.BuildingBlocks.DependencyInjection;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre le producteur Kafka et le registre de contrats.
    /// Le registre est explicite : on declare ce qu'on publie et ce qu'on ecoute,
    /// pas de scan d'assembly magique qui abonnerait un service a son insu.
    /// </summary>
    public static IServiceCollection AddForterroMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IntegrationEventRegistry> configureContracts)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configureContracts);

        services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(KafkaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var registry = new IntegrationEventRegistry();
        configureContracts(registry);
        services.AddSingleton(registry);

        services.AddSingleton<IEventPublisher, KafkaEventPublisher>();

        return services;
    }

    /// <summary>Active la boucle de consommation Kafka pour ce service.</summary>
    public static IServiceCollection AddForterroConsumer<TContext>(this IServiceCollection services)
        where TContext : DbContext, IInboxDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IProcessedEventStore, EfProcessedEventStore<TContext>>();
        services.AddHostedService<KafkaConsumerService>();

        return services;
    }

    /// <summary>
    /// Active l'Outbox : ecriture transactionnelle + dispatcher + purge.
    /// </summary>
    public static IServiceCollection AddForterroOutbox<TContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TContext : DbContext, IOutboxDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName));

        services.AddScoped<IOutboxWriter, OutboxWriter<TContext>>();
        services.AddHostedService<OutboxDispatcher<TContext>>();
        services.AddHostedService<OutboxCleanupService<TContext>>();

        return services;
    }

    /// <summary>Abonne un handler a un contrat entrant.</summary>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IIntegrationEventHandler<TEvent>, THandler>();
        return services;
    }
}
