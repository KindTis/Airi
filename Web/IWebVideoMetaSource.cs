using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;

namespace Airi.Web
{
    public interface IWebVideoMetaSource
    {
        string Name { get; }
        VideoMetadataSourceKind Kind { get; }
        string Host { get; }
        Task<WebVideoMetaResult> FetchAsync(
            MetadataProductCode code,
            bool includeThumbnail,
            ISet<string> blockedHosts,
            CancellationToken cancellationToken);
    }

    public enum VideoMetadataSourceKind
    {
        LibreDmm,
        JavBus,
        JavBusUncensored
    }
}
