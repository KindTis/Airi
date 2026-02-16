using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;

namespace Airi.Web
{
    public interface IWebVideoMetaSource
    {
        string Name { get; }
        bool CanHandle(string query);
        Task<WebVideoMetaResult?> FetchAsync(string query, CancellationToken cancellationToken);
    }
}
