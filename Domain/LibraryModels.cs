using System;
using System.Collections.Generic;

namespace Airi.Domain
{
    public record VideoMeta(
        string Title,
        DateOnly? Date,
        IReadOnlyList<string> Actors,
        string Thumbnail,
        IReadOnlyList<string> Tags,
        string Description);

    public record VideoEntry(
        string Path,
        VideoMeta Meta,
        long SizeBytes = 0,
        DateTime LastModifiedUtc = default,
        DateTime CreatedUtc = default);

    public class LibraryData
    {
        public int Version { get; set; } = 1;
        public List<VideoEntry> Videos { get; set; } = new();
    }
}
