using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;

namespace Monitor.Web.Otlp;

/// <summary>
/// OTLP/HTTP JSON uses the protobuf JSON mapping with a few protocol-specific deviations:
/// trace/span ids are hexadecimal, enums are numeric, and receivers ignore unknown fields.
/// This codec keeps Google.Protobuf responsible for protobuf semantics and normalizes only
/// those OTLP-specific wire differences around it.
/// </summary>
public static class OtlpJson
{
    private static readonly JsonParser Parser = new(
        JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default.WithFormatEnumsAsIntegers(true));

    private static readonly HashSet<string> EnumFieldNames = new(StringComparer.Ordinal)
    {
        "kind",
        "severityNumber",
        "aggregationTemporality",
        "code"
    };

    public static T Parse<T>(string json)
        where T : class, IMessage<T>, new()
    {
        var node = JsonNode.Parse(json)
            ?? throw new FormatException("The OTLP JSON payload is empty.");

        NormalizeForProtobufParser(node);
        return Parser.Parse<T>(node.ToJsonString());
    }

    public static string Format(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var node = JsonNode.Parse(Formatter.Format(message))
            ?? throw new InvalidOperationException("The protobuf JSON formatter returned an empty payload.");

        NormalizeForOtlpJson(node);
        return node.ToJsonString();
    }

    private static void NormalizeForProtobufParser(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (IsTraceOrSpanId(property.Key) && property.Value is JsonValue idValue)
                    {
                        if (!idValue.TryGetValue<string>(out var hex))
                        {
                            throw new FormatException($"OTLP JSON field '{property.Key}' must be a hexadecimal string.");
                        }

                        byte[] bytes;
                        try
                        {
                            bytes = Convert.FromHexString(hex);
                        }
                        catch (FormatException ex)
                        {
                            throw new FormatException($"OTLP JSON field '{property.Key}' is not valid hexadecimal.", ex);
                        }

                        obj[property.Key] = Convert.ToBase64String(bytes);
                        continue;
                    }

                    if (EnumFieldNames.Contains(property.Key) &&
                        property.Value is JsonValue enumValue &&
                        enumValue.TryGetValue<string>(out _))
                    {
                        throw new FormatException($"OTLP JSON enum field '{property.Key}' must use its integer value.");
                    }

                    NormalizeForProtobufParser(property.Value);
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    NormalizeForProtobufParser(item);
                }
                break;
        }
    }

    private static void NormalizeForOtlpJson(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (IsTraceOrSpanId(property.Key) && property.Value is JsonValue idValue)
                    {
                        if (!idValue.TryGetValue<string>(out var base64))
                        {
                            throw new InvalidOperationException($"Protobuf JSON field '{property.Key}' was not a string.");
                        }

                        byte[] bytes;
                        try
                        {
                            bytes = Convert.FromBase64String(base64);
                        }
                        catch (FormatException ex)
                        {
                            throw new InvalidOperationException($"Protobuf JSON field '{property.Key}' was not valid base64.", ex);
                        }

                        obj[property.Key] = Convert.ToHexString(bytes).ToLowerInvariant();
                        continue;
                    }

                    NormalizeForOtlpJson(property.Value);
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    NormalizeForOtlpJson(item);
                }
                break;
        }
    }

    private static bool IsTraceOrSpanId(string propertyName) =>
        string.Equals(propertyName, "traceId", StringComparison.Ordinal) ||
        string.Equals(propertyName, "spanId", StringComparison.Ordinal);
}
