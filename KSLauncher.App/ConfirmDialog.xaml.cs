using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KSLauncher.App;

public partial class ConfirmDialog : Window
{
    public bool UserConfirmed { get; private set; }

    public enum DialogKind
    {
        Info,
        Success,
        Warning,
        Error,
        Question,
        Destructive
    }

    public ConfirmDialog(
        string title, 
        string message, 
        string confirmText = "Понятно", 
        string cancelText = "Отмена", 
        DialogKind kind = DialogKind.Info, 
        Brush? accentBrush = null, 
        Brush? surfaceBrush = null, 
        Brush? borderBrush = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        if (surfaceBrush != null)
        {
            DialogBorder.Background = surfaceBrush;
            Background = surfaceBrush;
        }
        if (borderBrush != null)
        {
            DialogBorder.BorderBrush = borderBrush;
        }

        switch (kind)
        {
            case DialogKind.Destructive:
                ConfirmButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                ConfirmButton.Foreground = Brushes.White;
                IconBadge.Background = new SolidColorBrush(Color.FromArgb(50, 220, 38, 38));
                IconGlyph.Text = "🗑";
                break;

            case DialogKind.Error:
                ConfirmButton.Background = new SolidColorBrush(Color.FromRgb(225, 29, 72));
                ConfirmButton.Foreground = Brushes.White;
                IconBadge.Background = new SolidColorBrush(Color.FromArgb(50, 225, 29, 72));
                IconGlyph.Text = "✕";
                break;

            case DialogKind.Warning:
                ConfirmButton.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                ConfirmButton.Foreground = Brushes.White;
                IconBadge.Background = new SolidColorBrush(Color.FromArgb(50, 217, 119, 6));
                IconGlyph.Text = "⚠️";
                break;

            case DialogKind.Success:
                if (accentBrush != null)
                {
                    ConfirmButton.Background = accentBrush;
                    ConfirmButton.Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 20));
                }
                else
                {
                    ConfirmButton.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    ConfirmButton.Foreground = Brushes.White;
                }
                IconBadge.Background = new SolidColorBrush(Color.FromArgb(50, 16, 185, 129));
                IconGlyph.Text = "✓";
                break;

            case DialogKind.Question:
                if (accentBrush != null)
                {
                    ConfirmButton.Background = accentBrush;
                    ConfirmButton.Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 20));
                }
                IconBadge.Background = new SolidColorBrush(Color.FromArgb(40, 147, 51, 234));
                IconGlyph.Text = "❓";
                break;

            case DialogKind.Info:
            default:
                if (accentBrush != null)
                {
                    ConfirmButton.Background = accentBrush;
                    ConfirmButton.Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 20));
                }
                IconBadge.Background = new SolidColorBrush(Color.FromArgb(40, 6, 182, 212));
                IconGlyph.Text = "ℹ";
                break;
        }
    }

    private static (Brush? accent, Brush? surface, Brush? border) ExtractBrushes(Window? owner)
    {
        if (owner is MainWindow mw)
        {
            var accent = mw.Resources["Accent"] as Brush;
            var surface = mw.Resources["Panel"] as Brush ?? mw.Resources["SurfaceBase"] as Brush;
            var border = mw.Resources["SurfaceBorder"] as Brush;
            return (accent, surface, border);
        }
        return (null, null, null);
    }

    public static bool Show(Window? owner, string title, string message, string confirmText = "Да", string cancelText = "Отмена", bool isDestructive = false)
    {
        var (accent, surface, border) = ExtractBrushes(owner);
        var kind = isDestructive ? DialogKind.Destructive : DialogKind.Question;
        var dialog = new ConfirmDialog(title, message, confirmText, cancelText, kind, accent, surface, border);
        if (owner != null && owner.IsVisible) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.UserConfirmed;
    }

    public static void ShowInfo(Window? owner, string title, string message)
    {
        var (accent, surface, border) = ExtractBrushes(owner);
        var dialog = new ConfirmDialog(title, message, "Понятно", "", DialogKind.Info, accent, surface, border);
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        if (owner != null && owner.IsVisible) dialog.Owner = owner;
        dialog.ShowDialog();
    }

    public static void ShowSuccess(Window? owner, string title, string message)
    {
        var (accent, surface, border) = ExtractBrushes(owner);
        var dialog = new ConfirmDialog(title, message, "Отлично", "", DialogKind.Success, accent, surface, border);
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        if (owner != null && owner.IsVisible) dialog.Owner = owner;
        dialog.ShowDialog();
    }

    public static void ShowWarning(Window? owner, string title, string message)
    {
        var (accent, surface, border) = ExtractBrushes(owner);
        var dialog = new ConfirmDialog(title, message, "Понятно", "", DialogKind.Warning, accent, surface, border);
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        if (owner != null && owner.IsVisible) dialog.Owner = owner;
        dialog.ShowDialog();
    }

    public static void ShowError(Window? owner, string title, string message)
    {
        var (accent, surface, border) = ExtractBrushes(owner);
        var dialog = new ConfirmDialog(title, message, "Закрыть", "", DialogKind.Error, accent, surface, border);
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        if (owner != null && owner.IsVisible) dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        UserConfirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        UserConfirmed = false;
        Close();
    }
}
