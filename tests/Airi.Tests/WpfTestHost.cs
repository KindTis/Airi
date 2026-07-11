using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Airi.Tests;

internal static class WpfTestHost
{
    private static readonly ManualResetEventSlim Ready = new(false);
    private static readonly Thread Thread;
    private static Dispatcher? _dispatcher;
    private static Exception? _initializationFailure;

    static WpfTestHost()
    {
        Thread = new Thread(HostThreadMain)
        {
            IsBackground = true,
            Name = "Airi WPF test host"
        };
        Thread.SetApartmentState(ApartmentState.STA);
        Thread.Start();
    }

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RunAsync(() =>
        {
            action();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
    }

    public static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = GetDispatcher();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.BeginInvoke(async () =>
        {
            Exception? failure = null;
            try
            {
                await action();
                DrainDispatcher(dispatcher);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            var leakedWindows = Application.Current?.Windows.Cast<Window>().ToArray() ?? Array.Empty<Window>();
            foreach (var window in leakedWindows)
            {
                window.Close();
            }

            DrainDispatcher(dispatcher);

            if (failure is not null)
            {
                completion.TrySetException(failure);
            }
            else if (leakedWindows.Length > 0)
            {
                completion.TrySetException(new Xunit.Sdk.XunitException(
                    $"WPF test leaked {leakedWindows.Length} window(s): {string.Join(", ", leakedWindows.Select(window => window.GetType().Name))}"));
            }
            else
            {
                completion.TrySetResult();
            }
        }, DispatcherPriority.Normal);

        return completion.Task;
    }

    public static void DrainDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.VerifyAccess();

        PushUntil(dispatcher, DispatcherPriority.Background);
        PushUntil(dispatcher, DispatcherPriority.Render);
    }

    private static Dispatcher GetDispatcher()
    {
        Ready.Wait();
        if (_initializationFailure is not null)
        {
            throw new InvalidOperationException("The WPF test host could not be initialized.", _initializationFailure);
        }

        return _dispatcher ?? throw new InvalidOperationException("The WPF test dispatcher is unavailable.");
    }

    private static void PushUntil(Dispatcher dispatcher, DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(new Action(() => frame.Continue = false), priority);
        Dispatcher.PushFrame(frame);
    }

    private static void HostThreadMain()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            if (Application.Current is not null)
            {
                throw new InvalidOperationException("Application.Current already exists before WPF test host initialization.");
            }

            var application = new App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
                SuppressMainWindowCreation = true
            };
            application.InitializeComponent();
            _dispatcher = dispatcher;
            Ready.Set();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _initializationFailure = ex;
            Ready.Set();
        }
    }
}
