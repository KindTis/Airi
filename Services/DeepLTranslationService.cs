using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AppLogger.Info("DeepL translation cancelled by caller.");
                throw;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Info("DeepL translation timed out.");
            }
            catch (DeepLException ex)
            {
                AppLogger.Error($"DeepL translation failed ({ex.GetType().Name}).");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Unexpected error while translating with DeepL ({ex.GetType().Name}).");
            }

            AppLogger.Info("DeepL translation returning original text due to previous errors.");
            return text;
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        public async Task<IReadOnlyList<string?>> TranslateBatchAsync(IReadOnlyList<string> texts, string? sourceLanguageCode,
            string targetLanguageCode, bool protectMarkup, CancellationToken cancellationToken)
        {
            if (texts.Count == 1 && !protectMarkup)
                return [await TranslateAsync(texts[0], sourceLanguageCode, targetLanguageCode, cancellationToken).ConfigureAwait(false)];

            var options = new TextTranslateOptions { PreserveFormatting = true };
            if (protectMarkup)
            {
                options.TagHandling = "xml";
                options.IgnoreTags.Add("keep");
            }
            try
            {
                var results = (await _client.TranslateTextAsync(texts, sourceLanguageCode, targetLanguageCode, options, cancellationToken)
                    .ConfigureAwait(false)).ToArray();
                AppLogger.Info($"DeepL metadata batch: count={results.Length}, billed={results.Sum(result => result.BilledCharacters)}.");
                return results.Select(result => result.Text?.Trim()).ToArray();
            }
            catch (Exception error) when (error is DeepLException or OperationCanceledException or HttpRequestException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppLogger.Error($"DeepL metadata batch failed ({error.GetType().Name}).");
                return new string?[texts.Count];
            }
        }
    }
}
