using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSharp;

public interface IToolArguments
{
}

public static class ToolArgumentValidator
{
    public static bool TryValidateJson<T>(string argumentsJson, out T? args, out string? errorMessage)
        where T : class
    {
        args = null;
        errorMessage = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "arguments должен быть JSON-объектом с именованными параметрами";
                return false;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            args = JsonSerializer.Deserialize<T>(argumentsJson, options);
            if (args is null)
            {
                errorMessage = "не удалось распарсить arguments: проверь имена и типы полей";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            errorMessage = $"некорректный JSON в arguments: {ex.Message}";
            return false;
        }
    }
}
