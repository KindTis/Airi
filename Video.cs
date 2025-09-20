using System;
using System.Collections.Generic;
using System.Linq;

namespace Airi
{
    /// <summary>
    /// Minimal video metadata placeholder used for the Stage 0 offline prototype.
    /// </summary>
    public class VideoItem
    {
        public string Title { get; init; } = string.Empty;
        public DateOnly? ReleaseDate { get; init; }
        public IReadOnlyList<string> Actors { get; init; } = Array.Empty<string>();
        public string ThumbnailUri { get; init; } = string.Empty;
        public string SourcePath { get; init; } = string.Empty;

        public string ActorsLabel => Actors.Count == 0 ? string.Empty : string.Join(", ", Actors);
        public string ReleaseLabel => ReleaseDate?.ToString("yyyy-MM-dd") ?? "Date TBD";
    }
}
