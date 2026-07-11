using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Airi.Infrastructure;

internal sealed class AppStartupCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly Dispatcher _dispatcher;
    private readonly Func<CancellationToken, Task<IThumbnailImageLoader>> _loaderFactory;
    private readonly Func<IThumbnailImageLoader, Window> _windowFactory;
    private readonly Action<Exception> _logUnexpectedFailure;
    private readonly Action<int> _requestShutdown;
    private readonly CancellationTokenSource _startupCts = new();
    private Task? _startupTask;
    private bool _mainWindowCreated;

    public AppStartupCoordinator(
        Dispatcher dispatcher,
        Func<CancellationToken, Task<IThumbnailImageLoader>> loaderFactory,
        Func<IThumbnailImageLoader, Window> windowFactory,
        Action<Exception> logUnexpectedFailure,
        Action<int> requestShutdown)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _loaderFactory = loaderFactory ?? throw new ArgumentNullException(nameof(loaderFactory));
        _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
        _logUnexpectedFailure = logUnexpectedFailure ?? throw new ArgumentNullException(nameof(logUnexpectedFailure));
        _requestShutdown = requestShutdown ?? throw new ArgumentNullException(nameof(requestShutdown));
    }

    public Task StartAsync()
    {
        lock (_sync)
        {
            return _startupTask ??= StartCoreAsync();
        }
    }

    public void Cancel()
    {
        try
        {
            _startupCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        Cancel();
        _startupCts.Dispose();
    }

    private async Task StartCoreAsync()
    {
        try
        {
            var token = _startupCts.Token;
            var loader = await _loaderFactory(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested ||
                    _dispatcher.HasShutdownStarted ||
                    _dispatcher.HasShutdownFinished ||
                    _mainWindowCreated)
                {
                    return;
                }

                var window = _windowFactory(loader);
                if (Application.Current is not null)
                {
                    Application.Current.MainWindow = window;
                }
                window.Show();
                _mainWindowCreated = true;
            }, DispatcherPriority.Normal);
        }
        catch (OperationCanceledException) when (_startupCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await ObserveUnexpectedFailureAsync(ex).ConfigureAwait(false);
        }
    }

    private async Task ObserveUnexpectedFailureAsync(Exception exception)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            _logUnexpectedFailure(exception);
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _logUnexpectedFailure(exception);
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                _requestShutdown(-1);
            }
        }, DispatcherPriority.Normal);
    }
}
