using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Airi.Domain;

public enum GalleryView { All, Incomplete, Missing, Trash }
public enum GalleryFacet { Actors, Studio, Genres }
public readonly record struct GalleryFacetValue(string? Value, bool IsUnspecified = false)
{
    public static GalleryFacetValue Unspecified => new(null, true);
    public string Label => IsUnspecified ? "미지정(빈 값)" : Value ?? string.Empty;
}
public sealed record GalleryEntrySnapshot(VideoEntry Entry, VideoPresenceState Presence, bool IsTrash, bool NeedsAttention);
public sealed record LibrarySnapshot(IReadOnlyList<GalleryEntrySnapshot> Entries);
public sealed record GalleryQuery(GalleryView View, string Search,
    IReadOnlyList<GalleryFacetValue> Actors, IReadOnlyList<GalleryFacetValue> Studios,
    IReadOnlyList<GalleryFacetValue> Genres, string SortField, ListSortDirection SortDirection);
public sealed record GalleryFacetCount(GalleryFacetValue Value, int Count);
public sealed record GalleryQueryResult(IReadOnlyList<Guid> Ids,
    IReadOnlyList<GalleryFacetCount> Actors, IReadOnlyList<GalleryFacetCount> Studios,
    IReadOnlyList<GalleryFacetCount> Genres, int NormalCount, int IncompleteCount,
    int MissingCount, int TrashCount, int AttentionCount);

public static class GalleryQueryEngine
{
    public static GalleryQueryResult Evaluate(LibrarySnapshot snapshot, GalleryQuery query, CancellationToken token = default)
    {
        var search = Normalize(query.Search.Trim());
        var results = new List<GalleryEntrySnapshot>();
        var actorKnown = new List<GalleryFacetValue>();
        var studioKnown = new List<GalleryFacetValue>();
        var genreKnown = new List<GalleryFacetValue>();
        var actorCounts = new Dictionary<GalleryFacetValue, int>();
        var studioCounts = new Dictionary<GalleryFacetValue, int>();
        var genreCounts = new Dictionary<GalleryFacetValue, int>();
        var normalCount = 0;
        var incompleteCount = 0;
        var missingCount = 0;
        var trashIds = new HashSet<Guid>();
        var attentionIds = new HashSet<Guid>();

        static void Register(IEnumerable<GalleryFacetValue> values, List<GalleryFacetValue> known, Dictionary<GalleryFacetValue, int> counts)
        {
            foreach (var value in values) if (counts.TryAdd(value, 0)) known.Add(value);
        }
        static void Count(IEnumerable<GalleryFacetValue> values, Dictionary<GalleryFacetValue, int> counts)
        {
            foreach (var value in values) counts[value]++;
        }
        static IReadOnlyList<GalleryFacetCount> Finish(List<GalleryFacetValue> known, Dictionary<GalleryFacetValue, int> counts,
            IReadOnlyList<GalleryFacetValue> selected)
        {
            Register(selected, known, counts);
            Register(new[] { GalleryFacetValue.Unspecified }, known, counts);
            return known.OrderBy(value => value.IsUnspecified ? 1 : 0)
                .ThenBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase)
                .Select(value => new GalleryFacetCount(value, counts[value])).ToArray();
        }

        foreach (var item in snapshot.Entries)
        {
            token.ThrowIfCancellationRequested();
            var incomplete = !item.IsTrash && IsIncomplete(item.Entry.Meta);
            if (!item.IsTrash)
            {
                normalCount++;
                if (incomplete) incompleteCount++;
                if (item.Presence == VideoPresenceState.Missing) missingCount++;
            }
            else
            {
                trashIds.Add(item.Entry.Id);
                if (item.NeedsAttention) attentionIds.Add(item.Entry.Id);
            }
            var inView = query.View switch
            {
                GalleryView.Trash => item.IsTrash,
                GalleryView.Incomplete => incomplete,
                GalleryView.Missing => !item.IsTrash && item.Presence == VideoPresenceState.Missing,
                _ => !item.IsTrash
            };
            if (!inView) continue;

            var actors = Values(item.Entry.Meta, GalleryFacet.Actors).Distinct().ToArray();
            var studios = Values(item.Entry.Meta, GalleryFacet.Studio).Distinct().ToArray();
            var genres = Values(item.Entry.Meta, GalleryFacet.Genres).Distinct().ToArray();
            Register(actors, actorKnown, actorCounts);
            Register(studios, studioKnown, studioCounts);
            Register(genres, genreKnown, genreCounts);

            var matchesSearch = Search(item.Entry.Meta, search);
            var matchesActors = MatchesFacet(actors, query.Actors);
            var matchesStudio = MatchesFacet(studios, query.Studios);
            var matchesGenres = MatchesFacet(genres, query.Genres);
            if (!matchesSearch) continue;
            if (matchesActors && matchesStudio && matchesGenres) results.Add(item);
            if (matchesStudio && matchesGenres) Count(actors, actorCounts);
            if (matchesActors && matchesGenres) Count(studios, studioCounts);
            if (matchesActors && matchesStudio) Count(genres, genreCounts);
        }

        var sortedResults = results.ToArray();
        Array.Sort(sortedResults, (left, right) => Compare(left.Entry, right.Entry, query));
        token.ThrowIfCancellationRequested();
        return new GalleryQueryResult(sortedResults.Select(item => item.Entry.Id).ToArray(),
            Finish(actorKnown, actorCounts, query.Actors), Finish(studioKnown, studioCounts, query.Studios),
            Finish(genreKnown, genreCounts, query.Genres), normalCount, incompleteCount, missingCount,
            trashIds.Count, attentionIds.Count);
    }

    public static bool IsIncomplete(VideoMeta meta) => string.IsNullOrWhiteSpace(meta.Title) || meta.Date is null ||
        meta.Actors.Count == 0 || string.IsNullOrWhiteSpace(meta.Thumbnail) ||
        meta.Thumbnail.EndsWith("noimage.jpg", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(meta.Description) ||
        string.IsNullOrWhiteSpace(meta.Studio) || meta.Genres.Count == 0;

    private static IEnumerable<GalleryFacetValue> Values(VideoMeta meta, GalleryFacet facet)
    {
        var values = (facet switch
        {
            GalleryFacet.Actors => meta.Actors,
            GalleryFacet.Genres => meta.Genres,
            _ => string.IsNullOrWhiteSpace(meta.Studio) ? Array.Empty<string>() : new[] { meta.Studio! }
        }).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return values.Length == 0 ? new[] { GalleryFacetValue.Unspecified } : values.Select(value => new GalleryFacetValue(value));
    }
    private static bool MatchesFacet(IReadOnlyList<GalleryFacetValue> values, IReadOnlyList<GalleryFacetValue> selected) =>
        selected.Count == 0 || values.Any(value => selected.Contains(value));
    private static string Normalize(string? value) => (value ?? string.Empty).Normalize(NormalizationForm.FormC);
    private static bool Search(VideoMeta meta, string search) => search.Length == 0 ||
        (search == "미지정" && (IsIncomplete(meta) || meta.Tags.Count == 0)) ||
        new[] { meta.Title, meta.Studio }.Concat(meta.Actors).Concat(meta.Tags).Concat(meta.Genres)
            .Any(value => Normalize(value).Contains(search, StringComparison.OrdinalIgnoreCase));
    private static int Compare(VideoEntry left, VideoEntry right, GalleryQuery query)
    {
        var direction = query.SortDirection == ListSortDirection.Descending ? -1 : 1;
        int order;
        if (query.SortField == "ReleaseDate")
        {
            if (left.Meta.Date is null && right.Meta.Date is not null) return 1;
            if (left.Meta.Date is not null && right.Meta.Date is null) return -1;
            order = Nullable.Compare(left.Meta.Date, right.Meta.Date);
        }
        else if (query.SortField == "CreatedUtc") order = left.CreatedUtc.CompareTo(right.CreatedUtc);
        else order = StringComparer.CurrentCultureIgnoreCase.Compare(left.Meta.Title, right.Meta.Title);
        if (order != 0) return order * direction;
        order = StringComparer.CurrentCultureIgnoreCase.Compare(left.Meta.Title, right.Meta.Title);
        return order != 0 ? order : left.Id.CompareTo(right.Id);
    }
}
