using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;

namespace Airi.Services;

public interface ILibraryStore
{
    string FilePath { get; }
    Task<LibraryData> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LibraryData library, CancellationToken cancellationToken = default);
}
