using System.Threading;
using System.Threading.Tasks;

namespace Airi.Services
{
    public interface ITextTranslationService
    {
        bool IsEnabled { get; }

        Task<string?> TranslateAsync(
            string text,
            string? sourceLanguageCode,
            string targetLanguageCode,
            CancellationToken cancellationToken);
    }
}
