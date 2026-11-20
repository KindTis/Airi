using System.ComponentModel;
using Airi.Domain;
using Airi.Controls;
using System.Globalization;
using Airi.ViewModels;

namespace Airi.Tests;

public sealed class GalleryStateTests
{
    private static VideoEntry Entry(string title, string[] actors, string? studio, string[] genres, DateOnly? date = null) =>
        new("./" + title + ".mp4", new VideoMeta(title, date ?? new DateOnly(2024, 1, 2), actors, "cover.jpg", new[] { "tag" }, "description")
        { Studio = studio, Genres = genres });
    private static GalleryQuery Query(string search = "", GalleryView view = GalleryView.All,
        GalleryFacetValue[]? actors = null, GalleryFacetValue[]? studios = null, GalleryFacetValue[]? genres = null,
        string sort = "Title", ListSortDirection direction = ListSortDirection.Ascending) =>
        new(view, search, actors ?? [], studios ?? [], genres ?? [], sort, direction);
    private static LibrarySnapshot Snapshot(params VideoEntry[] entries) => new(entries.Select(entry =>
        new GalleryEntrySnapshot(entry, VideoPresenceState.Available, false, false)).ToArray());

    [Fact]
    public void Query_CombinesWithinFacetOrBetweenFacetsAndAndExcludesOwnFacetFromCounts()
    {
        var a = Entry("a", ["A", "B", "B"], "S1", ["G1"]);
        var b = Entry("b", ["B"], "S2", ["G2"]);
        var c = Entry("c", [], null, []);
        var result = GalleryQueryEngine.Evaluate(Snapshot(a, b, c), Query(actors: [new("A"), new("B")], studios: [new("S1")]));
        Assert.Equal(new[] { a.Id }, result.Ids);
        Assert.Equal(1, result.Actors.Single(value => value.Value.Value == "B").Count);
        Assert.Equal(1, result.Studios.Single(value => value.Value.Value == "S2").Count);
        Assert.Equal(0, result.Genres.Single(value => value.Value.Value == "G2").Count);
        var zero = GalleryQueryEngine.Evaluate(Snapshot(a, b), Query(studios: [new("missing")]));
        Assert.Empty(zero.Ids);
        Assert.Contains(zero.Studios, value => value.Value.Value == "missing" && value.Count == 0);
        var searched = GalleryQueryEngine.Evaluate(Snapshot(a, b, c), Query("S1"));
        Assert.Equal(new[] { a.Id }, searched.Ids);
        Assert.Contains(searched.Studios, value => value.Value.Value == "S2" && value.Count == 0);
        Assert.Contains(searched.Genres, value => value.Value.Value == "G2" && value.Count == 0);
    }

    [Fact]
    public void Query_UnspecifiedSentinelDiffersFromLiteralAndUnicodeSearchCoversAllFiveFields()
    {
        var literal = Entry("가나다", ["배우"], "미지정", ["장르"]);
        var empty = Entry("empty", [], null, []);
        var snapshot = Snapshot(literal, empty);
        Assert.Equal(new[] { empty.Id }, GalleryQueryEngine.Evaluate(snapshot, Query(studios: [GalleryFacetValue.Unspecified])).Ids);
        Assert.Equal(new[] { literal.Id }, GalleryQueryEngine.Evaluate(snapshot, Query(studios: [new("미지정")])).Ids);
        foreach (var search in new[] { "가나다".Normalize(System.Text.NormalizationForm.FormD), "배우", "tag", "미지정", "장르" })
            Assert.Contains(literal.Id, GalleryQueryEngine.Evaluate(snapshot, Query(search)).Ids);
    }

    [Fact]
    public void Query_TrashAndAttentionAreDistinctIdsAndMissingDatesAlwaysSortLast()
    {
        var dated = Entry("z", [], null, []);
        var missing = Entry("a", [], null, []) with { Meta = Entry("a", [], null, []).Meta with { Date = null, RawDate = "invalid" } };
        var trash = Entry("trash", [], null, []);
        var snapshot = new LibrarySnapshot(new[] {
            new GalleryEntrySnapshot(dated, VideoPresenceState.Missing, false, false),
            new GalleryEntrySnapshot(missing, VideoPresenceState.Available, false, false),
            new GalleryEntrySnapshot(trash, VideoPresenceState.Missing, true, true) });
        foreach (var direction in Enum.GetValues<ListSortDirection>())
        {
            var result = GalleryQueryEngine.Evaluate(snapshot, Query(sort: "ReleaseDate", direction: direction));
            Assert.Equal(new[] { dated.Id, missing.Id }, result.Ids);
            Assert.Equal(1, result.MissingCount); Assert.Equal(1, result.TrashCount); Assert.Equal(1, result.AttentionCount);
        }
        Assert.Equal(new[] { trash.Id }, GalleryQueryEngine.Evaluate(snapshot, Query(view: GalleryView.Trash)).Ids);
    }

    [Fact]
    public void SingleDraft_OnlyChangedFieldAndItsLockChangeAndRawLegacyValuesStayUnchanged()
    {
        var entry = Entry("  title  ", [" actor ", " actor ", ""], null, ["genre"]) with
        { Meta = Entry("  title  ", [" actor ", " actor ", ""], null, ["genre"]).Meta with
            { Date = null, RawDate = "legacy-invalid", Tags = new[] { " tag ", " tag " }, ManualFieldLocks = new[] { MetadataField.Title } } };
        using var draft = new GalleryEditorDraft(new[] { entry }, new VideoItem { Title = entry.Meta.Title });
        Assert.False(draft.IsBatch);
        Assert.Equal("  title  ", draft.Fields.Single(field => field.Field == MetadataField.Title).Value);
        draft.Fields.Single(field => field.Field == MetadataField.Studio).Value = "  studio  ";
        var candidate = draft.BuildCandidates()![entry.Id];
        Assert.Equal("studio", candidate.Meta.Studio);
        Assert.Equal(entry.Meta.Title, candidate.Meta.Title);
        Assert.Equal(entry.Meta.Actors, candidate.Meta.Actors);
        Assert.Equal(entry.Meta.Tags, candidate.Meta.Tags);
        Assert.Equal("legacy-invalid", candidate.Meta.RawDate);
        Assert.Equal(new[] { MetadataField.Studio }, draft.Changes.Select(change => change.Field));
        Assert.Contains(MetadataField.Title, candidate.Meta.ManualFieldLocks);
        Assert.DoesNotContain(MetadataField.Actors, candidate.Meta.ManualFieldLocks);
    }

    [Fact]
    public void BatchDraft_StartsOffAndEmpty_OnlyEnabledFieldsApplyAndTagsAppendWithoutRewritingLegacy()
    {
        var a = Entry("a", ["existing"], "old", ["old-genre"]) with { Meta = Entry("a", ["existing"], "old", ["old-genre"]).Meta with { Tags = new[] { " old ", " old " } } };
        var b = Entry("b", ["different"], "different", []);
        using var draft = new GalleryEditorDraft(new[] { a, b }, new VideoItem());
        Assert.True(draft.IsBatch);
        Assert.All(draft.Fields, field => { Assert.False(field.IsApplied); Assert.Equal("", field.Value); });
        Assert.DoesNotContain(draft.Fields, field => field.Field is MetadataField.Title or MetadataField.Description or MetadataField.Thumbnail);
        draft.Fields.Single(field => field.Field == MetadataField.Actors).Value = "ignored";
        var studio = draft.Fields.Single(field => field.Field == MetadataField.Studio); studio.IsApplied = true;
        var tags = draft.Fields.Single(field => field.Field == MetadataField.Tags); tags.IsApplied = true; tags.Value = "new，new, other";
        var result = draft.BuildCandidates()!;
        Assert.Equal(a.Meta.Actors, result[a.Id].Meta.Actors);
        Assert.Null(result[a.Id].Meta.Studio);
        Assert.Equal(new[] { " old ", " old ", "new", "other" }, result[a.Id].Meta.Tags);
        Assert.Contains(MetadataField.Studio, result[a.Id].Meta.ManualFieldLocks);
        Assert.Equal(a.Meta.Title, result[a.Id].Meta.Title);
    }

    [Fact]
    public void SingleDraft_ExplicitEmptyLockIsVisibleWithoutChangingOtherFields()
    {
        var entry = Entry("title", [], null, []);
        using var draft = new GalleryEditorDraft(new[] { entry }, new VideoItem());
        draft.Fields.Single(field => field.Field == MetadataField.Studio).IsLocked = true;
        var candidate = draft.BuildCandidates()![entry.Id];
        Assert.Null(candidate.Meta.Studio);
        Assert.Contains(MetadataField.Studio, candidate.Meta.ManualFieldLocks);
        var change = Assert.Single(draft.Changes); Assert.True(change.LockChanged);
        var converter = new GalleryChangeLabelConverter();
        Assert.Equal("미지정", converter.Convert(change, typeof(string), "Before", CultureInfo.CurrentCulture));
        Assert.Equal("빈 값 유지 · 수동 보호 변경", converter.Convert(change, typeof(string), "After", CultureInfo.CurrentCulture));
        var cleared = new GalleryChange("title", MetadataField.Studio, "studio", "", true);
        Assert.Equal("studio", converter.Convert(cleared, typeof(string), "Before", CultureInfo.CurrentCulture));
        Assert.Equal("미지정", converter.Convert(cleared, typeof(string), "After", CultureInfo.CurrentCulture));
    }
}
