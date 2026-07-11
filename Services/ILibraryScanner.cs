using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;

namespace Airi.Services;

public interface ILibraryScanner
{
    Task<LibraryScanResult> ScanAsync(LibraryData library, CancellationToken cancellationToken);
}
