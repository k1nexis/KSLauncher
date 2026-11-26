using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace KSLauncher.Installer;

public partial class MainWindow : Window
{
    private string _installedExePath = "";

    public MainWindow()
    {
        InitializeComponent();
        SafeLoadAppIcon();
        InitDefaultPaths();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_SETICON = 0x0080;
    private static readonly IntPtr ICON_SMALL = IntPtr.Zero;
    private static readonly IntPtr ICON_BIG = (IntPtr)1;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (sysIcon != null)
                {
                    SendMessage(hwnd, WM_SETICON, ICON_SMALL, sysIcon.Handle);
                    SendMessage(hwnd, WM_SETICON, ICON_BIG, sysIcon.Handle);
                }
            }
        }
        catch { }
    }

    private void SafeLoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (sysIcon != null)
                {
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
            }
        }
        catch { }
    }

    private void InitDefaultPaths()
    {
        // Если есть диск D:, предлагаем его сразу, чтобы сберечь место на диске C:
        if (Directory.Exists("D:\\"))
        {
            InstallFolderBox.Text = @"D:\Games\KSLauncher";
            GameFolderBox.Text = @"D:\Games\KSLauncher\game";
        }
        else
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var defaultInstall = Path.Combine(localAppData, "Programs", "KSLauncher");
            InstallFolderBox.Text = defaultInstall;
            GameFolderBox.Text = Path.Combine(defaultInstall, "game");
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BrowseInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку для установки KS Launcher",
            InitialDirectory = Directory.Exists(InstallFolderBox.Text) ? InstallFolderBox.Text : "C:\\"
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            InstallFolderBox.Text = dialog.FolderName;
            // Если папка игры ещё внутри старой папки установки, синхронизируем
            if (GameFolderBox.Text.EndsWith("\\game", StringComparison.OrdinalIgnoreCase))
            {
                GameFolderBox.Text = Path.Combine(dialog.FolderName, "game");
            }
        }
    }

    private void BrowseGameFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку для файлов Minecraft (любой диск)",
            InitialDirectory = Directory.Exists(GameFolderBox.Text) ? GameFolderBox.Text : "C:\\"
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            GameFolderBox.Text = dialog.FolderName;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var installDir = InstallFolderBox.Text.Trim();
        var gameDir = GameFolderBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(installDir))
        {
            MessageBox.Show(this, "Пожалуйста, укажите папку для установки лаунчера.", "Установка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(gameDir))
        {
            MessageBox.Show(this, "Пожалуйста, укажите папку для файлов игры Minecraft.", "Установка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        InstallButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.IsIndeterminate = true;
        StatusLabel.Visibility = Visibility.Visible;
        StatusLabel.Text = "Подготовка папок назначения...";

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(installDir);
                Directory.CreateDirectory(gameDir);

                // Завершаем запущенные копии лаунчера, чтобы не было ошибки занятого файла
                try
                {
                    foreach (var p in Process.GetProcessesByName("KSLauncher"))
                    {
                        try { p.Kill(); p.WaitForExit(1000); } catch { }
                    }
                }
                catch { }

                // 1. Распаковка исполняемых файлов из ресурсов
                Dispatcher.Invoke(() => StatusLabel.Text = "Копирование исполняемых файлов KS Launcher...");
                var assembly = Assembly.GetExecutingAssembly();

                var targetExe = Path.Combine(installDir, "KSLauncher.exe");
                ExtractResource(assembly, "KSLauncher.exe", targetExe);

                var targetDll = Path.Combine(installDir, "WebView2Loader.dll");
                ExtractResource(assembly, "WebView2Loader.dll", targetDll);

                _installedExePath = targetExe;

                // 2. Создание файла настроек launcher-settings.json
                Dispatcher.Invoke(() => StatusLabel.Text = "Настройка конфигурации и путей Minecraft...");
                SaveInitialSettings(installDir, gameDir);

                // 3. Создание ярлыка на рабочем столе
                bool createDesktop = false;
                bool createStartMenu = false;
                Dispatcher.Invoke(() =>
                {
                    createDesktop = DesktopShortcutCheck.IsChecked == true;
                    createStartMenu = StartMenuShortcutCheck.IsChecked == true;
                });

                if (createDesktop)
                {
                    Dispatcher.Invoke(() => StatusLabel.Text = "Создание ярлыка на рабочем столе...");
                    var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    var linkPath = Path.Combine(desktopPath, "KS Launcher.lnk");
                    CreateShortcut(linkPath, targetExe, installDir, "KS Launcher — Minecraft Desktop");
                }

                // 4. Создание ярлыка в меню Пуск
                if (createStartMenu)
                {
                    Dispatcher.Invoke(() => StatusLabel.Text = "Создание ярлыка в меню Пуск...");
                    var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    var linkPath = Path.Combine(startMenu, "KS Launcher.lnk");
                    CreateShortcut(linkPath, targetExe, installDir, "KS Launcher — Minecraft Desktop");
                }
            });

            // Показ экрана успеха
            SetupContentGrid.Visibility = Visibility.Collapsed;
            SuccessGrid.Visibility = Visibility.Visible;
            SuccessInstallPathText.Text = installDir;
            SuccessGamePathText.Text = gameDir;

            if (LaunchAfterInstallCheck.IsChecked == true)
            {
                LaunchInstalledApp();
            }
        }
        catch (Exception ex)
        {
            InstallButton.IsEnabled = true;
            InstallProgress.Visibility = Visibility.Collapsed;
            StatusLabel.Text = "Ошибка установки: " + ex.Message;
            MessageBox.Show(this, $"Произошла ошибка при установке:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void ExtractResource(Assembly assembly, string resourceName, string targetPath)
    {
        // Поиск ресурса по имени
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

        if (name == null) return;

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream == null) return;

        using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.CopyTo(fileStream);
    }

    private static void SaveInitialSettings(string installDir, string gameDir)
    {
        var settingsObj = new
        {
            BackgroundPath = (string?)null,
            IncludeSnapshots = false,
            AccentColor = "#9333EA",
            GradientAccent = true,
            BackgroundDim = 0.70,
            SecondaryAccentColor = "#06B6D4",
            GameLanguage = "ru",
            JavaPath = (string?)null,
            ElyWorkerUrl = "https://kslauncher-api.nerck522.workers.dev/",
            GameDirectory = gameDir,
            PanelOpacity = 0.85
        };

        var json = JsonSerializer.Serialize(settingsObj, new JsonSerializerOptions { WriteIndented = true });

        // Сохраняем в папку установки
        try
        {
            File.WriteAllText(Path.Combine(installDir, "launcher-settings.json"), json);
        }
        catch { }

        // Сохраняем в %APPDATA%\KSLauncher\launcher-settings.json
        try
        {
            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher");
            Directory.CreateDirectory(appDataDir);
            File.WriteAllText(Path.Combine(appDataDir, "launcher-settings.json"), json);
        }
        catch { }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                var shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = workingDirectory;
                shortcut.Description = description;
                shortcut.IconLocation = targetPath + ",0";
                shortcut.Save();
            }
        }
        catch { }
    }

    private void LaunchApp_Click(object sender, RoutedEventArgs e)
    {
        LaunchInstalledApp();
        Close();
    }

    private void LaunchInstalledApp()
    {
        try
        {
            if (File.Exists(_installedExePath))
            {
                Process.Start(new ProcessStartInfo(_installedExePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(_installedExePath),
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }
}
