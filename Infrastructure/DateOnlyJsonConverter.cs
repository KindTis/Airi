using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airi.Infrastructure
{
    /// <summary>
    /// Handles converting DateOnly values to ISO 8601 dates within System.Text.Json.
    /// </summary>
    public sealed class DateOnlyJsonConverter : JsonConverter<DateOnly>
    {
        private const string Format = "yyyy-MM-dd";

        public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && DateOnly.TryParse(reader.GetString(), out var value))
            {
                return value;
            }

            throw new JsonException("Invalid DateOnly format.");
        }

        public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString(Format));
        }
    }
}
