using System.Windows;
using System.Windows.Media;
using KSLauncher.App.Services;

namespace KSLauncher.App;

public partial class LogWindow : Window
{
    private readonly GameLogService _logs;

    public LogWindow(GameLogService logs)
    {
        InitializeComponent();
        _logs = logs;
        Loaded += OnLoaded;
        RefreshText();
        _logs.MessageReceived += OnMessageReceived;
        Closed += (_, _) => _logs.MessageReceived -= OnMessageReceived;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow mw)
        {
            if (mw.Resources["Accent"] is Brush accent)
                Resources["Accent"] = accent;
            if (mw.Resources["SurfaceBorder"] is Brush border)
                Resources["SurfaceBorder"] = border;
        }
    }

    private void RefreshText()
    {
        var text = _logs.GetFullLogText();
        LogTextBox.Text = text;
        LineCountLabel.Text = $"Строк: {_logs.GetLogs().Count}";
        if (AutoScrollBox.IsChecked == true)
        {
            LogTextBox.ScrollToEnd();
        }
    }

    private void OnMessageReceived(string msg, bool isError)
    {
        Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var line = $"[{timestamp}] {(isError ? "[ERROR] " : "")}{msg}\n";
            LogTextBox.AppendText(line);
            LineCountLabel.Text = $"Строк: {_logs.GetLogs().Count}";
            if (AutoScrollBox.IsChecked == true)
            {
                LogTextBox.ScrollToEnd();
            }
        });
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(LogTextBox.Text);
        ConfirmDialog.ShowSuccess(this, "Консоль логов", "Логи Minecraft успешно скопированы в буфер обмена.");
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        LogTextBox.Clear();
        LineCountLabel.Text = "Строк: 0";
    }
}
