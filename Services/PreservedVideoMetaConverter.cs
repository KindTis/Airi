using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Airi.Domain;

namespace Airi.Services;

// Date keeps the legacy JSON string; parsed DateOnly is an independent UI/query value.
internal sealed class PreservedVideoMetaConverter : JsonConverter<VideoMeta>
{
    private sealed record Payload(
        string Title, string? Date, IReadOnlyList<ActorName> Actors, string Thumbnail,
        IReadOnlyList<string> Tags, string Description)
    {
        public string? Studio { get; init; }
        public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
        public string? RawDate { get; init; }
        public KoreanMetadata? Korean { get; init; }
        public IReadOnlyList<MetadataField> ManualFieldLocks { get; init; } = Array.Empty<MetadataField>();
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
    }

    public override VideoMeta Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var payload = JsonSerializer.Deserialize<Payload>(ref reader, options)
            ?? throw new JsonException("Metadata cannot be null.");
        if (payload.Actors is null || payload.Actors.Any(actor => actor is null))
            throw new JsonException("Invalid actor values. The original library is preserved.");
        if (payload.RawDate is not null && payload.Date is not null && payload.RawDate != payload.Date)
            throw new JsonException("Conflicting raw and parsed release date. The original library is preserved.");
        DateOnly? date = DateOnly.TryParseExact(payload.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : null;
        return new VideoMeta(payload.Title, date, payload.Actors.Select(actor => actor.OriginalName).ToArray(), payload.Thumbnail, payload.Tags, payload.Description)
        {
            Studio = payload.Studio,
            Genres = payload.Genres,
            ActorNames = payload.Actors.Where(actor => actor.Reading is not null || actor.KoreanName is not null).ToArray(),
            Korean = payload.Korean,
            RawDate = payload.RawDate ?? (payload.Date is not null && !date.HasValue ? payload.Date : null),
            ManualFieldLocks = payload.ManualFieldLocks,
            AdditionalData = payload.AdditionalData
        };
    }

    public override void Write(Utf8JsonWriter writer, VideoMeta value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Payload(value.Title,
            value.RawDate ?? value.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            MetadataDisplay.Actors(value), value.Thumbnail, value.Tags, value.Description)
        {
            Studio = value.Studio,
            Genres = value.Genres,
            Korean = value.Korean,
            RawDate = value.RawDate,
            ManualFieldLocks = value.ManualFieldLocks,
            AdditionalData = value.AdditionalData
        }, options);
}

internal sealed class ActorNameConverter : JsonConverter<ActorName>
{
    private sealed record Payload(string OriginalName, string? Reading, string? KoreanName);

    public override ActorName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new ActorName(reader.GetString()!);
        var actor = JsonSerializer.Deserialize<Payload>(ref reader, options);
        if (actor?.OriginalName is null) throw new JsonException("Actor original name is required.");
        return new ActorName(actor.OriginalName, actor.Reading, actor.KoreanName);
    }

    public override void Write(Utf8JsonWriter writer, ActorName actor, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Payload(actor.OriginalName, actor.Reading, actor.KoreanName), options);
}
