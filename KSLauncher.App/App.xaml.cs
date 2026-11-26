using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace KSLauncher.App;

public partial class App : Application
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KSLauncher");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Directory.CreateDirectory(LogDir);
            File.WriteAllText(Path.Combine(LogDir, "launcher_last_run.log"),
                $"KSLauncher started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nOS: {Environment.OSVersion}\nRuntime: {Environment.Version}\n");
        }
        catch { }

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogAndShowError("Dispatcher Error", e.Exception);
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogAndShowError("Fatal AppDomain Error", ex);
        }
    }

    private static void LogAndShowError(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var logPath = Path.Combine(LogDir, "launcher_error.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}]\n{ex}\n\n");
        }
        catch { }

        MessageBox.Show(
            $"Произошла непредвиденная ошибка в лаунчере:\n\n{ex.Message}\n\nПодробности записаны в {Path.Combine(LogDir, "launcher_error.log")}",
            "KS Launcher — Ошибка",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
