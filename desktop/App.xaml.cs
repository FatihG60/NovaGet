using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MyIdm.Desktop;

public partial class App : Application
{
    private const string MutexName = @"Local\NovaGet_SingleInstance_Mutex_8F9E3A7C";
    private const string ActivateEventName = @"Local\NovaGet_ActivateEvent_8F9E3A7C";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _registeredWait;

    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaGet", "crash.log");

    public static void Log(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {source}: {ex}\n\n");
        }
        catch
        {
            // Logging must never throw
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Wake up the already-running NovaGet instance (even if minimized to System Tray) and exit
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                evt.Set();
            }
            catch { }

            Shutdown(0);
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (Current?.MainWindow is MainWindow mw)
                    {
                        mw.RestoreFromTray();
                    }
                });
            },
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: false);

        DispatcherUnhandledException += (_, args) =>
        {
            Log("Dispatcher", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log("AppDomain", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("UnobservedTask", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log("OnStartup", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _registeredWait?.Unregister(null);
            _activateEvent?.Dispose();
            if (_instanceMutex is not null)
            {
                _instanceMutex.ReleaseMutex();
                _instanceMutex.Dispose();
            }
        }
        catch { }

        base.OnExit(e);
    }
}
