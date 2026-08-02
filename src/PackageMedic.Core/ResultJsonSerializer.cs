using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackageMedic.Core;

public static class ResultJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(AnalysisResult result) => JsonSerializer.Serialize(result, Options);
}
