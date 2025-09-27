using System;
using System.Collections.Generic;

namespace Airi.Domain
{
    public record TargetFolder(
        string Root,
        IReadOnlyList<string> IncludePatterns,
        IReadOnlyList<string> ExcludePatterns,
        DateTime? LastScanUtc);

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
        DateTime LastModifiedUtc = default);

    public class LibraryData
    {
        public int Version { get; set; } = 1;
        public List<TargetFolder> Targets { get; set; } = new();
        public List<VideoEntry> Videos { get; set; } = new();
    }
}
