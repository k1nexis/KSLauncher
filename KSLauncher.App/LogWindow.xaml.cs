using System.Windows;
using KSLauncher.App.Services;

namespace KSLauncher.App;

public partial class LogWindow : Window
{
    private readonly GameLogService _logs;

    public LogWindow(GameLogService logs)
    {
        InitializeComponent();
        _logs = logs;
        RefreshText();
        _logs.MessageReceived += OnMessageReceived;
        Closed += (_, _) => _logs.MessageReceived -= OnMessageReceived;
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
        MessageBox.Show(this, "Логи скопированы в буфер обмена.", "Консоль логов", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        _logs.Clear();
        LogTextBox.Clear();
        LineCountLabel.Text = "Строк: 0";
    }
}
