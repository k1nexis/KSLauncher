using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KSLauncher.App;

public enum UpdateDialogResult
{
    UpdateNow,
    GoToSettings,
    RemindLater,
    Dismissed
}

public partial class UpdateNotificationDialog : Window
{
    public UpdateDialogResult Result { get; private set; } = UpdateDialogResult.Dismissed;

    public UpdateNotificationDialog(Window? owner, string latestVersion, string message)
    {
        InitializeComponent();

        TitleText.Text = $"🆕 Доступно обновление v{latestVersion}";
        MessageText.Text = message;

        // Применяем тему из MainWindow если есть
        if (owner is MainWindow mw)
        {
            if (mw.Resources["Panel"] is Brush surface || mw.Resources["SurfaceBase"] is Brush surface2)
            {
                var brush = mw.Resources["Panel"] as Brush ?? mw.Resources["SurfaceBase"] as Brush;
                if (brush != null) { DialogBorder.Background = brush; Background = brush; }
            }
            if (mw.Resources["SurfaceBorder"] is Brush border)
                DialogBorder.BorderBrush = border;
            if (mw.Resources["Accent"] is Brush accent)
                UpdateButton.Background = accent;
        }

        if (owner != null && owner.IsVisible)
            Owner = owner;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        Result = UpdateDialogResult.UpdateNow;
        Close();
    }

    private void GoToSettings_Click(object sender, RoutedEventArgs e)
    {
        Result = UpdateDialogResult.GoToSettings;
        Close();
    }

    private void RemindLater_Click(object sender, RoutedEventArgs e)
    {
        Result = UpdateDialogResult.RemindLater;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Result = UpdateDialogResult.Dismissed;
        Close();
    }
}
