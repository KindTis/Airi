using Airi.Domain;

namespace Airi.Web
{
    public sealed record WebVideoMetaResult(
        VideoMeta Meta,
        byte[]? ThumbnailBytes,
        string? ThumbnailExtension);
}
