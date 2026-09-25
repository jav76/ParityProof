using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParityProof.Engine.Reporting;

// Hashes are written as hex strings because JSON numbers above 2^53 are silently rounded by JavaScript and jq.
public sealed class HexUInt64JsonConverter : JsonConverter<ulong>
{
    private const string HEX_PREFIX = "0x";

    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetUInt64();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();
            if (text is not null
                && text.StartsWith(HEX_PREFIX, StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(
                    text.AsSpan(HEX_PREFIX.Length),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out ulong value))
            {
                return value;
            }
        }

        throw new JsonException("Expected a 0x-prefixed hexadecimal string for a 64-bit hash value.");
    }

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(ReportFormatting.FormatHash(value));
    }
}
