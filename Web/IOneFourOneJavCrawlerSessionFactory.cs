using System;
using System.Threading;
using System.Threading.Tasks;

namespace Airi.Web
{
    public interface IOneFourOneJavCrawlerSessionHandle : IDisposable
    {
        IOneFourOneJavCrawlerSession Session { get; }
        bool IsBrowserOpen();
    }

    public sealed record OneFourOneJavCrawlerStartResult(
        IOneFourOneJavCrawlerSessionHandle Handle,
        IOneFourOneJavCrawlerSession Session,
        string Summary);

    public interface IOneFourOneJavCrawlerSessionFactory
    {
        Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default);
    }
}
