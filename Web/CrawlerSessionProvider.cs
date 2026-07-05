namespace Airi.Web
{
    public sealed class CrawlerSessionProvider : IOneFourOneJavCrawlerSessionProvider
    {
        public IOneFourOneJavCrawlerSession? CurrentSession { get; private set; }

        public void SetSession(IOneFourOneJavCrawlerSession? session)
        {
            CurrentSession = session;
        }
    }
}
