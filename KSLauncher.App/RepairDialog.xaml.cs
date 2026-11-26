using System.Windows;
using System.Windows.Input;

namespace KSLauncher.App;

public partial class RepairDialog : Window
{
    public RepairDialog(string instanceName, string explanation, string logContent)
    {
        InitializeComponent();
        InstanceInfoText.Text = $"Сборка: {instanceName}";
        ExplanationText.Text = explanation;
        LogTextBox.Text = logContent;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogTextBox.Text);
            MessageBox.Show(this, "Лог ошибки скопирован в буфер обмена!", "Скопировано", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
