using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forterro.BuildingBlocks.Api;

/// <summary>Options JSON exposees sur le contrat HTTP public.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
