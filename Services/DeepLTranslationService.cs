using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeepL;
using Airi.Infrastructure;

namespace Airi.Services
{
    public sealed class DeepLTranslationService : ITextTranslationService, IDisposable
    {
        private readonly DeepLClient _client;

        public DeepLTranslationService(string authKey, DeepLClientOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(authKey))
            {
                throw new ArgumentException("DeepL authentication key must be provided.", nameof(authKey));
            }

            var effectiveOptions = options ?? new DeepLClientOptions();
            if (effectiveOptions.appInfo is null)
            {
                effectiveOptions.appInfo = new AppInfo
                {
                    AppName = "Airi",
                    AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0"
                };
            }

            _client = new DeepLClient(authKey, effectiveOptions);
        }

        public bool IsEnabled => true;

        public async Task<string?> TranslateAsync(
            string text,
            string? sourceLanguageCode,
            string targetLanguageCode,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                AppLogger.Info("DeepL translation skipped: input text is empty.");
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(targetLanguageCode))
            {
                throw new ArgumentException("Target language code must be provided.", nameof(targetLanguageCode));
            }

            try
            {
                var options = new TextTranslateOptions
                {
                    PreserveFormatting = true
                };

                AppLogger.Info($"DeepL translation request: target={targetLanguageCode}, source={(sourceLanguageCode ?? "auto")}, length={text.Length}.");

                var result = await _client.TranslateTextAsync(
                    text,
                    sourceLanguageCode,
                    targetLanguageCode,
                    options,
                    cancellationToken).ConfigureAwait(false);

                AppLogger.Info($"DeepL translation succeeded: detected={result.DetectedSourceLanguageCode ?? sourceLanguageCode ?? "unknown"}, billed={result.BilledCharacters}.");

                return result.Text?.Trim();
            }
            catch (OperationCanceledException)
            {
                AppLogger.Info("DeepL translation cancelled by caller.");
                throw;
            }
            catch (DeepLException ex)
            {
                AppLogger.Error("DeepL translation failed.", ex);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unexpected error while translating with DeepL.", ex);
            }

            AppLogger.Info("DeepL translation returning original text due to previous errors.");
            return text;
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
