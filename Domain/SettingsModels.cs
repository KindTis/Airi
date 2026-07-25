using System.Collections.Generic;

namespace Airi.Domain;

public sealed record TargetFolder(
    string Root,
    IReadOnlyList<string> IncludePatterns,
    IReadOnlyList<string> ExcludePatterns);

public sealed class SortSettings
{
    public string Field { get; set; } = "ReleaseDate";
    public string Direction { get; set; } = "Descending";
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public List<TargetFolder> Targets { get; set; } = new();
    public SortSettings Sort { get; set; } = new();
}
