using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

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

        async Task<IReadOnlyList<string?>> TranslateBatchAsync(IReadOnlyList<string> texts, string? sourceLanguageCode,
            string targetLanguageCode, bool protectMarkup, CancellationToken cancellationToken)
        {
            var results = new string?[texts.Count];
            for (var index = 0; index < results.Length; index++)
                results[index] = await TranslateAsync(texts[index], sourceLanguageCode, targetLanguageCode, cancellationToken).ConfigureAwait(false);
            return results;
        }
    }
}
