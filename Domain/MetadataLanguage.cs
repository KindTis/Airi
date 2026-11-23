using System;
using System.Collections.Generic;
using System.Linq;

namespace Airi.Domain;

public sealed record ActorName(string OriginalName, string? Reading = null, string? KoreanName = null)
{
    public string DisplayName => KoreanName ?? (OriginalName.Any(character => character is >= '가' and <= '힣')
        ? OriginalName : "한글명 미지정");
}

public sealed record KoreanMetadata
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, string>? Genres { get; init; }
    public IReadOnlyDictionary<string, string>? Tags { get; init; }

    public static string Label(IReadOnlyDictionary<string, string>? values, string original) =>
        values?.GetValueOrDefault(original) ?? original;

    internal KoreanMetadata? OrNull() => this == new KoreanMetadata() ? null : this;
}

internal static class MetadataDisplay
{
    internal static IReadOnlyList<ActorName> Actors(VideoMeta meta)
    {
        var names = meta.ActorNames.DistinctBy(actor => actor.OriginalName).ToDictionary(actor => actor.OriginalName, StringComparer.Ordinal);
        return meta.Actors.Select(name => names.GetValueOrDefault(name) ?? new ActorName(name)).ToArray();
    }
}
