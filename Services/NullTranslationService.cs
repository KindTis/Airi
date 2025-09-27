using System.Threading;
using System.Threading.Tasks;

namespace Airi.Services
{
    public sealed class NullTranslationService : ITextTranslationService
    {
        public static ITextTranslationService Instance { get; } = new NullTranslationService();

        private NullTranslationService()
        {
        }

        public bool IsEnabled => false;

        public Task<string?> TranslateAsync(
            string text,
            string? sourceLanguageCode,
            string targetLanguageCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(text);
        }
    }
}
