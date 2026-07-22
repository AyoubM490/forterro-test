using System.Runtime.CompilerServices;
using Forterro.BuildingBlocks.Banking;
using Forterro.BuildingBlocks.Messaging;
using Forterro.BuildingBlocks.Messaging.Kafka;
using Forterro.BuildingBlocks.Observability;
using Forterro.BuildingBlocks.Outbox;

// Compatibilite BINAIRE apres l'extraction de Forterro.Messaging.Abstractions.
//
// Ces six types etaient publics dans l'assembly Forterro.BuildingBlocks en 1.0.0.
// Ils vivent desormais dans un autre assembly. Le namespace n'ayant pas bouge, tout
// RECOMPILE sans modification — mais un assembly deja compile contre la 1.0.0 porte
// une reference vers `[Forterro.BuildingBlocks]IEventPublisher`, qui n'existe plus.
// Sans redirection, il leve un TypeLoadException a l'execution, et seulement a
// l'execution : ni le build ni les tests de l'appelant ne le detectent.
//
// TypeForwardedTo inscrit dans l'ancien assembly une redirection vers le nouveau.
// Un binaire de 2024 continue de fonctionner sans etre recompile.
//
// A retirer uniquement lors d'une version MAJEURE assumee, quand on accepte
// d'obliger tous les consommateurs a recompiler.
[assembly: TypeForwardedTo(typeof(IEventPublisher))]
[assembly: TypeForwardedTo(typeof(IIntegrationEventHandler<>))]
[assembly: TypeForwardedTo(typeof(IProcessedEventStore))]
[assembly: TypeForwardedTo(typeof(IntegrationEvent))]
[assembly: TypeForwardedTo(typeof(IntegrationEventRegistry))]
[assembly: TypeForwardedTo(typeof(MessagingJson))]

// Extraction de Forterro.Banking.
[assembly: TypeForwardedTo(typeof(Iban))]

// Extraction de Forterro.Diagnostics.
[assembly: TypeForwardedTo(typeof(Telemetry))]

// Extraction de Forterro.Messaging.Kafka.
[assembly: TypeForwardedTo(typeof(KafkaConsumerService))]
[assembly: TypeForwardedTo(typeof(KafkaEventPublisher))]
[assembly: TypeForwardedTo(typeof(KafkaOptions))]

// Extraction de Forterro.Outbox.EntityFrameworkCore.
[assembly: TypeForwardedTo(typeof(IOutboxWriter))]
[assembly: TypeForwardedTo(typeof(IOutboxDbContext))]
[assembly: TypeForwardedTo(typeof(OutboxMessage))]
[assembly: TypeForwardedTo(typeof(OutboxOptions))]
[assembly: TypeForwardedTo(typeof(OutboxWriter<>))]
[assembly: TypeForwardedTo(typeof(OutboxDispatcher<>))]
[assembly: TypeForwardedTo(typeof(OutboxCleanupService<>))]
