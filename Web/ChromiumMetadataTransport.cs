using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using PuppeteerSharp;

namespace Airi.Web;

internal sealed class ChromiumMetadataTransport(
    IBrowser browser, IPage page, DirectoryInfo profile, ISet<string> blockedHosts, CancellationToken sessionToken)
    : IMetadataProviderTransport
{
    private const int StartupTimeoutMs = 10_000;
    private const int NavigationTimeoutMs = 15_000;
    private const int RenderTimeoutMs = 8_000;
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);
    private volatile bool _closing;
    private string? _navigationFailure;

    public static async Task<IMetadataProviderTransport> OpenAsync(ISet<string> blockedHosts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = FindInstalledBrowser() ?? throw new BrowserNotInstalledException();
        var profile = Directory.CreateTempSubdirectory("Airi-Metadata-");
        IBrowser? browser = null;
        try
        {
            token.ThrowIfCancellationRequested();
            browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                ExecutablePath = executable,
                Headless = true,
                UserDataDir = profile.FullName,
                Timeout = StartupTimeoutMs,
                ProtocolTimeout = StartupTimeoutMs,
                WaitForInitialPage = false,
                IgnoredDefaultArgs = ["--disable-popup-blocking"],
                Args = ["--mute-audio"],
                DownloadBehavior = new DownloadBehavior { Policy = DownloadPolicy.Deny }
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var page = await browser.NewPageAsync().WaitAsync(BodyTimeout, token).ConfigureAwait(false);
            var transport = new ChromiumMetadataTransport(browser, page, profile, blockedHosts, token);
            page.AddRequestInterceptor(transport.InterceptRequestAsync);
            page.Response += transport.OnResponse;
            await page.SetBypassServiceWorkerAsync(true).WaitAsync(BodyTimeout, token).ConfigureAwait(false);
            await page.SetRequestInterceptionAsync(true).WaitAsync(BodyTimeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return transport;
        }
        catch
        {
            await CleanUpAsync(browser, profile).ConfigureAwait(false);
            throw;
        }
    }

    public Task<MetadataResource<string>> GetHtmlAsync(
        Uri uri, ISet<string> blockedHosts, CancellationToken token, string? readySelector = null) =>
        NavigateAsync(uri, _ => ReadHtmlAsync(readySelector, token), blockedHosts, token);

    private async Task<string> ReadHtmlAsync(string? readySelector, CancellationToken token)
    {
        if (readySelector is not null)
        {
            try
            {
                await using var ready = await page.WaitForSelectorAsync(
                    $"{readySelector}, #challenge-form, #cf-challenge-running, #challenge-stage",
                    new WaitForSelectorOptions { Timeout = RenderTimeoutMs, CancellationToken = token }).ConfigureAwait(false);
            }
            catch (WaitTaskTimeoutException)
            {
                var document = await page.GetContentAsync().ConfigureAwait(false);
                if (MetadataProviderLookup.IsChallengeDocument(document)) return document;
                throw new RenderTimeoutException();
            }
        }
        return await page.GetContentAsync().ConfigureAwait(false);
    }

    public Task<MetadataResource<byte[]>> GetCoverAsync(Uri uri, ISet<string> blockedHosts, CancellationToken token) =>
        NavigateAsync(uri, response => response.BufferAsync().AsTask(), blockedHosts, token);

    private async Task<MetadataResource<T>> NavigateAsync<T>(
        Uri uri, Func<IResponse, Task<T>> read, ISet<string> hosts, CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested();
        if (MetadataProviderLookup.RequestFailure(uri, hosts) is { } preflightFailure)
            return new MetadataResource<T>(uri, Failure: preflightFailure);

        Volatile.Write(ref _navigationFailure, null);
        try
        {
            var response = await page.GoToAsync(uri.AbsoluteUri, new NavigationOptions
            {
                CancellationToken = token,
                Timeout = NavigationTimeoutMs,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (response is null || !Uri.TryCreate(response.Url, UriKind.Absolute, out var finalUri)
                || finalUri.Scheme != Uri.UriSchemeHttps || finalUri.UserInfo.Length > 0)
                return new MetadataResource<T>(uri, Failure: $"Chromium 응답을 확인할 수 없음 ({uri.Host})");
            uri = finalUri;
            if (Volatile.Read(ref _navigationFailure) is { } navigationFailure)
                return new MetadataResource<T>(uri, Failure: navigationFailure);
            if (ReadAccessRestriction(response, uri) is { } restriction)
                return new MetadataResource<T>(uri, Failure: restriction);
            if (response.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return new MetadataResource<T>(uri);
            if (!response.Ok)
                return new MetadataResource<T>(uri, Failure: $"HTTP {(int)response.Status} 응답 ({uri.Host})");

            var content = await read(response).WaitAsync(BodyTimeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _navigationFailure) is { } bodyFailure)
                return new MetadataResource<T>(uri, Failure: bodyFailure);
            var mediaType = response.Headers.GetValueOrDefault("content-type")?.Split(';', 2)[0].Trim();
            return new MetadataResource<T>(uri, content, mediaType);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            token.ThrowIfCancellationRequested();
            var failure = Volatile.Read(ref _navigationFailure) ?? (exception switch
            {
                RenderTimeoutException => $"페이지 렌더링 시간 초과 ({uri.Host})",
                TimeoutException or OperationCanceledException => $"응답 시간 초과 ({uri.Host})",
                _ => $"Chromium 응답을 읽을 수 없음 ({uri.Host})"
            });
            return new MetadataResource<T>(uri, Failure: failure);
        }
    }

    private Task InterceptRequestAsync(IRequest request)
    {
        if (_closing)
            return request.AbortAsync(RequestAbortErrorCode.BlockedByClient);

        return RouteRequestAsync(request.Url, blockedHosts, sessionToken, () => request.ContinueAsync(), failure =>
        {
            if (IsMainNavigation(request))
                Interlocked.CompareExchange(ref _navigationFailure, failure, null);
            return request.AbortAsync(RequestAbortErrorCode.BlockedByClient);
        });
    }

    internal static Task RouteRequestAsync(
        string url, ISet<string> blockedHosts, CancellationToken token, Func<Task> allow, Func<string, Task> deny)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return deny("유효한 HTTPS 요청 주소를 확인할 수 없음");

        // This interceptor runs for each hop and subresource before Chromium sends it.
        lock (blockedHosts)
        {
            if (token.IsCancellationRequested)
                return deny("Chromium 조회가 취소됨");
            var failure = MetadataProviderLookup.RequestFailure(uri, blockedHosts);
            return failure is null ? allow() : deny(failure);
        }
    }

    private void OnResponse(object? sender, ResponseCreatedEventArgs args)
    {
        if (_closing || !Uri.TryCreate(args.Response.Url, UriKind.Absolute, out var uri))
            return;

        var failure = ReadAccessRestriction(args.Response, uri);
        if (failure is not null && IsMainNavigation(args.Response.Request))
            Interlocked.CompareExchange(ref _navigationFailure, failure, null);
    }

    private bool IsMainNavigation(IRequest request) => request.IsNavigationRequest && request.Frame == page.MainFrame;

    private string? ReadAccessRestriction(IResponse response, Uri uri)
    {
        var challenge = response.Headers.TryGetValue("cf-mitigated", out var mitigation)
            && mitigation.Split(',', StringSplitOptions.TrimEntries).Contains("challenge", StringComparer.OrdinalIgnoreCase);
        return MetadataProviderLookup.RecordAccessRestriction(uri, response.Status, challenge, blockedHosts);
    }

    internal static bool IsTransportFailure(Exception exception) =>
        exception is PuppeteerException or ProcessException or TimeoutException or IOException or UnauthorizedAccessException
            or Win32Exception or OperationCanceledException;

    internal sealed class BrowserNotInstalledException : IOException { }
    private sealed class RenderTimeoutException : TimeoutException { }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        await CleanUpAsync(browser, profile).ConfigureAwait(false);
    }

    private static async Task CleanUpAsync(IBrowser? browser, DirectoryInfo profile)
    {
        try
        {
            if (browser is not null)
            {
                var process = browser.Process;
                try
                {
                    await browser.CloseAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsTransportFailure(exception))
                {
                    Trace.TraceWarning("Metadata Chromium close failed: {0}", exception.Message);
                }

                if (process is { HasExited: false })
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }
                    await process.WaitForExitAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (IsTransportFailure(exception) || exception is InvalidOperationException)
        {
            Trace.TraceWarning("Metadata Chromium process cleanup failed: {0}", exception.Message);
        }
        finally
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    profile.Delete(recursive: true);
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 10)
                    {
                        Trace.TraceWarning("Metadata Chromium profile cleanup failed: {0}", exception.Message);
                        break;
                    }
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
        }
    }

    private static string? FindInstalledBrowser()
    {
        var locations = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        string[] candidates = [@"Google\Chrome\Application\chrome.exe", @"Microsoft\Edge\Application\msedge.exe"];
        return candidates.SelectMany(candidate => locations.Where(root => root.Length > 0)
            .Select(root => Path.Combine(root, candidate))).FirstOrDefault(File.Exists);
    }
}
