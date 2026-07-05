using System.Threading;
using System.Threading.Tasks;

namespace Airi.Web
{
    public interface IOneFourOneJavCrawlerSession
    {
        Task<bool> NavigateToAsync(string url, CancellationToken cancellationToken = default);
        Task<OneFourOneJavCrawler.CrawlerMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken = default);
        Task<string?> TryGetThumbnailUrlAsync(CancellationToken cancellationToken = default);
    }

    public interface IOneFourOneJavCrawlerSessionProvider
    {
        IOneFourOneJavCrawlerSession? CurrentSession { get; }
    }
}
