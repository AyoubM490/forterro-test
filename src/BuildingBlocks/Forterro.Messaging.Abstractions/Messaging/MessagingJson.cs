using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forterro.BuildingBlocks.Messaging;

/// <summary>
/// Serialisation figee pour les contrats inter-services.
/// Volontairement isolee des options JSON de l'API HTTP : les deux evoluent
/// independamment et un changement d'API ne doit pas modifier le format sur le bus.
/// </summary>
public static class MessagingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Tolerant a la lecture : un producteur plus recent peut ajouter un champ
        // sans casser les consommateurs deja deployes (compatibilite ascendante).
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };
}
