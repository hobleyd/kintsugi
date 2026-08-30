using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kintsugi.Domain.Common;

/// <summary>
/// Reads an enum from either its C# member name (e.g. "DirectDownload") or a snake_case
/// equivalent (e.g. "direct_download"), matching case-insensitively either way. Needed because
/// the AI research prompt asks the model for snake_case values, but that same JSON shape is also
/// accepted hand-typed or pasted verbatim into the "Save Script" flow, which otherwise binds
/// straight through <see cref="JsonStringEnumConverter"/> and only recognizes exact member names.
/// Writes using the normal member name.
/// </summary>
public sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"Cannot convert null or empty string to {typeToConvert.Name}.");
        }

        var normalized = value.Replace("_", "").Replace("-", "");
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        throw new JsonException($"'{value}' is not a valid {typeToConvert.Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
