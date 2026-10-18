using System.Windows;
using System.Windows.Threading;
using FfxRandomizer.Core;
using FfxRandomizer.Core.Storage;

namespace FfxRandomizer.App;

public partial class App : Application
{
    public static string CrashLogPath => Path.Combine(AppPaths.Default.Root, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        MessageBox.Show($"FFX Randomizer ran into a problem and needs to close.\n\n{e.Exception.Message}\n\nDetails were saved to {CrashLogPath}",
            "FFX Randomizer", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private static void LogCrash(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Default.Root);
            File.AppendAllText(CrashLogPath, $"[{DateTimeOffset.Now:u}] FFX Randomizer {AppInfo.Version}\n{exception}\n\n");
        }
        catch (IOException)
        {
        }
    }
}
