using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Airi.Domain;
using Airi.Web;

namespace Airi.Services;

// Keep authored timestamp text until a file probe actually changes that timestamp.
internal sealed class PreservedVideoEntryConverter : JsonConverter<VideoEntry>
{
    private sealed record Payload(string Path, VideoMeta Meta, long SizeBytes = 0,
        string? LastModifiedUtc = null, string? CreatedUtc = null)
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public long Revision { get; init; }
        public string? ProductCode { get; init; }
        public LibraryFilePresence Presence { get; init; }
        public DateTime? PresenceProbedAtUtc { get; init; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
    }
    private static DateTime Parse(string? text) => text is null ? default :
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Text(DateTime value, string? original) => original is not null && Parse(original) == value && Parse(original).Kind == value.Kind ? original :
        value.ToString("O", CultureInfo.InvariantCulture);
    public override VideoEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var payload = JsonSerializer.Deserialize<Payload>(ref reader, options) ?? throw new JsonException("Entry cannot be null.");
        try
        {
            var entry = new VideoEntry(payload.Path, payload.Meta, payload.SizeBytes, Parse(payload.LastModifiedUtc), Parse(payload.CreatedUtc))
            { Id = payload.Id, Revision = payload.Revision, Presence = payload.Presence, PresenceProbedAtUtc = payload.PresenceProbedAtUtc,
                ProductCode = payload.ProductCode,
                OriginalLastModifiedText = payload.LastModifiedUtc, OriginalCreatedText = payload.CreatedUtc, AdditionalData = payload.AdditionalData };
            return entry.ProductCode is null ? entry with { ProductCode = MetadataProductCode.FromEntry(entry)?.Value } : entry;
        }
        catch (FormatException ex) { throw new JsonException("Invalid file timestamp. The original library is preserved.", ex); }
    }
    public override void Write(Utf8JsonWriter writer, VideoEntry value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Payload(value.Path, value.Meta, value.SizeBytes,
            Text(value.LastModifiedUtc, value.OriginalLastModifiedText), Text(value.CreatedUtc, value.OriginalCreatedText))
        { Id = value.Id, Revision = value.Revision, Presence = value.Presence, PresenceProbedAtUtc = value.PresenceProbedAtUtc,
            ProductCode = value.ProductCode ?? MetadataProductCode.FromEntry(value)?.Value,
            AdditionalData = value.AdditionalData }, options);
}
