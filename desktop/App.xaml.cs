using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace MyIdm.Desktop;

public partial class App : Application
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIDM", "crash.log");

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
        DispatcherUnhandledException += (_, args) =>
        {
            Log("Dispatcher", args.Exception);
            args.Handled = true; // Keep the UI alive; the failing download shows its own error state
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
    }
}
