using Airi.Domain;

namespace Airi.Web
{
    public sealed record WebVideoMetaResult(
        VideoMeta? Meta,
        byte[]? ThumbnailBytes = null,
        string? ThumbnailExtension = null,
        string? Detail = null,
        bool Failed = false);
}
