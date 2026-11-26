using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using KSLauncher.App.Services;
using CmlLib.Core.Auth;

namespace KSLauncher.App;

public sealed record UnifiedModItem(
    string Id,
    string Slug,
    string Title,
    string Description,
    long Downloads,
    string ProjectType,
    string Platform,
    string PlatformColor,
    object RawProject,
    string Loader = "Fabric",
    string Version = "1.20.4"
);

public partial class MainWindow : Window
{
    private readonly MinecraftService _minecraft = new();
    private readonly ModrinthService _modrinth = new();
    private readonly CurseForgeService _curseforge = new();
    private readonly MinecraftVersionService _versions = new();
    private readonly OfflineProfileStore _profiles = new();
    private readonly AccountManager _accountManager = new();
    private readonly MicrosoftAccountService _microsoft = new();
    private readonly ElyAccountService _ely = new();
    private readonly AuthlibInjectorService _authlibInjector = new();
    private readonly CloudAccountService _cloud = new();
    private readonly FriendsService _friends = new();
    private readonly LocalServerService _localServers = new();
    private LocalServerConfig? _selectedLocalServer;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "launcher-settings.json");

    private MSession? _activeMicrosoftSession;
    private MSession? _activeElySession;
    private IReadOnlyList<MinecraftVersion> _allVersions = [];
    private bool _uiReady;
    private bool _suppressVersionReload;
    private string? _backgroundPath;
    private string _accentColor = "#9333EA";
    private string _secondaryAccentColor = "#06B6D4";
    private string _pendingAccentColor = "#9333EA";
    private string _pendingSecondaryAccentColor = "#06B6D4";
    private double _backgroundDim = 0.70;
    private double _panelOpacity = 0.85;
    private string? _gameDirectory;
    private string _fxType = "none";
    private int _fxDensity = 60;
    private double _fxSpeed = 1.0;
    private bool _gradientAccent = true;
    private bool _pendingGradientAccent = true;
    private bool _selectingSecondaryWheel;
    private bool _suppressSettingsWrites;
    private bool _suppressAccountSync;
    private string _gameLanguage = "ru";
    private string? _activeCustomInstancePath;
    private string? _selectedJavaPath;
    private string _elyWorkerUrl = "https://kslauncher-api.nerck522.workers.dev/";

    private Process? _runningGameProcess;
    private string? _runningInstancePath;
    private bool _userRequestedStop;
    private string? _lastCrashReport;
    private string? _lastCrashExplanation;
    private string? _lastCrashInstancePath;
    private string? _lastCrashVersion;
    private string? _lastCrashLoader;
    private string? _lastCrashInstanceName;

    public MainWindow()
    {
        InitializeComponent();
        SafeLoadAppIcon();
        _uiReady = true;
        HardwareLabel.Text = JvmTuning.HardwareSummary;
        RamSlider.Value = JvmTuning.RecommendedHeapMb / 1024.0;
        PrimaryColorWheelImage.Source = CreateColorWheelBitmap(180);
        SecondaryColorWheelImage.Source = CreateColorWheelBitmap(180);
        ApplyAccentTheme();
        UpdatePendingColorPreview();
        InitializeSocialAndServers();
        _ = InitializeAsync();
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

    private async Task InitializeAsync()
    {
        await RestoreProfileAsync();
        await RestoreSettingsAsync();
        await LoadVersionsAsync();
        LoadLibraryInstances();
        LoadJavaRuntimes();
        LoadSavedAccounts();
        _ = SendTelemetryAsync("launch");
        _ = RefreshServerLivePingAsync();
        _ = CheckStartupUpdateAsync();
    }

    private async Task RefreshServerLivePingAsync()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestHeaders.Add("User-Agent", "KSLauncher/0.2.0");
            string? serverIp = null;
            string serverName = "KS Network · Выживание";
            string serverDesc = "Официальный сервер KS Network";
            bool serverEnabled = false;

            try
            {
                var homeContent = await client.GetStringAsync("https://kslauncher-api.nerck522.workers.dev/v1/content/home");
                using var homeDoc = System.Text.Json.JsonDocument.Parse(homeContent);
                var rootEl = homeDoc.RootElement;
                if (rootEl.TryGetProperty("serverEnabled", out var seEl))
                {
                    serverEnabled = seEl.GetBoolean();
                }
                else
                {
                    serverEnabled = true;
                }

                if (rootEl.TryGetProperty("featuredServer", out var fs) && fs.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (fs.TryGetProperty("ip", out var ipEl) && !string.IsNullOrWhiteSpace(ipEl.GetString()))
                    {
                        serverIp = ipEl.GetString();
                    }
                    if (fs.TryGetProperty("name", out var nameEl) && !string.IsNullOrWhiteSpace(nameEl.GetString()))
                    {
                        serverName = nameEl.GetString()!;
                    }
                    if (fs.TryGetProperty("desc", out var descEl) && !string.IsNullOrWhiteSpace(descEl.GetString()))
                    {
                        serverDesc = descEl.GetString()!;
                    }
                }
                else
                {
                    serverEnabled = false;
                }
            }
            catch
            {
                serverEnabled = false;
            }

            if (!serverEnabled || string.IsNullOrWhiteSpace(serverIp))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ServerBlockHeader.Visibility = Visibility.Collapsed;
                    ServerCardBorder.Visibility = Visibility.Collapsed;
                });
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                ServerBlockHeader.Visibility = Visibility.Visible;
                ServerCardBorder.Visibility = Visibility.Visible;
                ServerIpText.Text = serverIp;
                ServerDescText.Text = serverDesc;
            });

            var response = await client.GetStringAsync($"https://api.mcsrvstat.us/3/{serverIp}");
            using var doc = System.Text.Json.JsonDocument.Parse(response);
            var root = doc.RootElement;
            bool isOnline = root.TryGetProperty("online", out var onlineEl) && onlineEl.GetBoolean();

            await Dispatcher.InvokeAsync(() =>
            {
                if (isOnline)
                {
                    int onlineCount = root.TryGetProperty("players", out var pl) && pl.TryGetProperty("online", out var onl) ? onl.GetInt32() : 0;
                    int maxCount = pl.TryGetProperty("max", out var mx) ? mx.GetInt32() : 500;
                    ServerStatusDot.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                    ServerStatusText.Text = $"В сети · {onlineCount} / {maxCount} онлайн";
                    ServerStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                    ServerSubtext.Text = "⚡ Сервер доступен для игры";
                }
                else
                {
                    ServerStatusDot.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139));
                    ServerStatusText.Text = "Пока что никто не играет (0 / 500 онлайн)";
                    ServerStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
                    ServerSubtext.Text = "⚡ Сервера на техобслуживании";
                }
            });
        }
        catch { }
    }

    private async Task SendTelemetryAsync(string eventType, string? customVersion = null, string? customLoader = null)
    {
        try
        {
            var machineGuid = Environment.MachineName + "_" + Environment.UserName;
            var sessionId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(machineGuid)));
            var version = customVersion ?? (VersionBox.SelectedItem as MinecraftVersion)?.Id ?? "1.20.1";
            var loader = customLoader ?? (GameLoaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "vanilla";
            var os = Environment.OSVersion.VersionString;

            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var payload = new
            {
                event_type = eventType,
                session_id = sessionId,
                version = version,
                loader = loader,
                os = os
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            using var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            await client.PostAsync("https://kslauncher-api.nerck522.workers.dev/v1/telemetry/event", content);
        }
        catch { }
    }

    private async Task LoadVersionsAsync()
    {
        PlayButton.IsEnabled = false;
        StatusText.Text = "Загружаю полный список официальных релизов Minecraft…";
        try
        {
            _allVersions = await _versions.GetVersionsAsync(IncludeSnapshotsBox.IsChecked == true);
            VersionBox.ItemsSource = _allVersions;
            VersionBox.SelectedIndex = 0;
            ModVersionBox.ItemsSource = _allVersions;
            ModVersionBox.SelectedIndex = 0;
            NewInstanceVersionBox.ItemsSource = _allVersions;
            NewInstanceVersionBox.SelectedIndex = 0;
            ServerVersionBox.ItemsSource = _allVersions;
            ServerVersionBox.SelectedIndex = 0;

            StatusText.Text = IncludeSnapshotsBox.IsChecked == true
                ? $"Доступно версий Minecraft, включая snapshots и старые: {_allVersions.Count}."
                : $"Доступно релизов Minecraft: {_allVersions.Count}.";
            PlayButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Не удалось загрузить каталог версий: {ex.Message}";
        }
    }

    private async Task RestoreProfileAsync()
    {
        var profile = await _profiles.LoadAsync();
        if (profile is null) return;
        OfflineNameBox.Text = profile.Username;
        SidebarProfile.Text = $"{profile.Username} • Offline";
    }

    private async Task RestoreSettingsAsync()
    {
        _suppressSettingsWrites = true;
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var settings = JsonSerializer.Deserialize<LauncherSettings>(await File.ReadAllTextAsync(_settingsPath));
            _suppressVersionReload = true;
            IncludeSnapshotsBox.IsChecked = settings?.IncludeSnapshots == true;
            _suppressVersionReload = false;

            _accentColor = IsHexColor(settings?.AccentColor) ? settings!.AccentColor! : "#9333EA";
            _secondaryAccentColor = IsHexColor(settings?.SecondaryAccentColor) ? settings!.SecondaryAccentColor! : "#06B6D4";
            _pendingAccentColor = _accentColor;
            _pendingSecondaryAccentColor = _secondaryAccentColor;
            _gradientAccent = settings?.GradientAccent ?? true;
            _pendingGradientAccent = _gradientAccent;
            GradientAccentBox.IsChecked = _pendingGradientAccent;
            UpdateColorWheelLayout();
            ApplyAccentTheme();
            UpdatePendingColorPreview();

            _backgroundDim = settings?.BackgroundDim is >= 0.05 and <= 0.9 ? settings.BackgroundDim : 0.70;
            BackgroundDimSlider.Value = _backgroundDim;
            BackgroundScrim.Opacity = _backgroundDim;
            if (BackgroundDimValueLabel != null) BackgroundDimValueLabel.Text = $"{(int)(_backgroundDim * 100)}%";

            _panelOpacity = settings?.PanelOpacity is >= 0.15 and <= 1.0 ? settings.PanelOpacity : 0.85;
            if (PanelOpacitySlider != null) PanelOpacitySlider.Value = _panelOpacity;
            if (PanelOpacityValueLabel != null) PanelOpacityValueLabel.Text = $"{(int)(_panelOpacity * 100)}%";

            if (!string.IsNullOrWhiteSpace(settings?.GameDirectory))
            {
                _gameDirectory = settings.GameDirectory;
                _minecraft.SetInstancesRoot(_gameDirectory);
                if (GameDirectoryBox != null) GameDirectoryBox.Text = _gameDirectory;
                if (GameDirectoryStatusText != null) GameDirectoryStatusText.Text = $"Игровая папка: {_gameDirectory}";
            }
            else
            {
                if (GameDirectoryBox != null) GameDirectoryBox.Text = _minecraft.InstancesRoot;
            }

            if (!string.IsNullOrWhiteSpace(settings?.FxType))
            {
                _fxType = settings.FxType;
                foreach (ComboBoxItem item in FxTypeBox.Items)
                {
                    if (item.Tag?.ToString() == _fxType)
                    {
                        FxTypeBox.SelectedItem = item;
                        break;
                    }
                }
            }

            _fxDensity = settings?.FxDensity is >= 20 and <= 150 ? settings.FxDensity : 60;
            if (FxDensitySlider != null) FxDensitySlider.Value = _fxDensity;
            if (FxDensityLabel != null) FxDensityLabel.Text = $"{_fxDensity} шт.";

            _fxSpeed = settings?.FxSpeed is >= 0.4 and <= 2.5 ? settings.FxSpeed : 1.0;
            if (FxSpeedSlider != null) FxSpeedSlider.Value = _fxSpeed;
            if (FxSpeedLabel != null) FxSpeedLabel.Text = $"{_fxSpeed:F1}x";

            UpdateFxHost();

            if (!string.IsNullOrWhiteSpace(settings?.GameLanguage))
            {
                _gameLanguage = settings.GameLanguage;
                foreach (ComboBoxItem item in GameLanguageBox.Items)
                {
                    if (item.Tag?.ToString() == _gameLanguage)
                    {
                        GameLanguageBox.SelectedItem = item;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(settings?.JavaPath) && File.Exists(settings.JavaPath))
            {
                _selectedJavaPath = settings.JavaPath;
            }

            if (!string.IsNullOrWhiteSpace(settings?.BackgroundPath) && File.Exists(settings.BackgroundPath))
            {
                ApplyBackground(settings.BackgroundPath);
            }

            if (!string.IsNullOrWhiteSpace(settings?.ElyWorkerUrl))
            {
                _elyWorkerUrl = settings.ElyWorkerUrl;
                ElyWorkerUrlBox.Text = _elyWorkerUrl;
                CloudUrlBox.Text = _elyWorkerUrl.TrimEnd('/');
            }
            else
            {
                ElyWorkerUrlBox.Text = _elyWorkerUrl; // default
                CloudUrlBox.Text = _elyWorkerUrl.TrimEnd('/');
            }
        }
        catch (Exception ex) { BackgroundStatusText.Text = $"Не удалось открыть сохранённый фон: {ex.Message}"; }
        finally { _suppressSettingsWrites = false; }
    }

    private async Task SaveSettingsAsync(string? backgroundPath)
    {
        _backgroundPath = backgroundPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await File.WriteAllTextAsync(_settingsPath, JsonSerializer.Serialize(new LauncherSettings(
            backgroundPath,
            IncludeSnapshotsBox.IsChecked == true,
            _accentColor,
            _gradientAccent,
            _backgroundDim,
            _secondaryAccentColor,
            _gameLanguage,
            _selectedJavaPath,
            _elyWorkerUrl,
            _gameDirectory,
            _panelOpacity,
            _fxType,
            _fxDensity,
            _fxSpeed
        ), new JsonSerializerOptions { WriteIndented = true }));
    }

    // ==========================================
    // ЗАПУСК ИГРЫ И КОНСОЛЬ ЛОГОВ
    // ==========================================

    private void GoToLibrary_Click(object sender, RoutedEventArgs e) => NavigateTo(1);

    private void CopyServerIp_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText("play.kslauncher.ru");
        StatusText.Text = "IP сервера play.kslauncher.ru скопирован в буфер обмена!";
        ConfirmDialog.ShowSuccess(this, "Сервер скопирован", "IP-адрес официального сервера play.kslauncher.ru скопирован в буфер обмена. Заходи играть!");
    }

    private void UpdateRunningStateUI()
    {
        Dispatcher.Invoke(() =>
        {
            bool isRunning = _runningGameProcess != null && !_runningGameProcess.HasExited;
            StopGameButton.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
            LibraryRunningBanner.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
            if (isRunning)
            {
                PlayButton.Visibility = Visibility.Collapsed;
                LibraryRunningTitleText.Text = $"Сборка сейчас запущена (PID {_runningGameProcess!.Id})";
            }
            else
            {
                PlayButton.Visibility = Visibility.Visible;
                PlayButton.IsEnabled = true;
            }
        });
    }

    private void StopGame_Click(object sender, RoutedEventArgs e)
    {
        if (_runningGameProcess == null || _runningGameProcess.HasExited)
        {
            _runningGameProcess = null;
            UpdateRunningStateUI();
            return;
        }

        var confirm = ConfirmDialog.Show(this, "Остановка игры",
            "Принудительно закрыть зависшее окно или фоновый процесс Minecraft?\nВсе несохранённые изменения в мире могут быть потеряны.",
            "Остановить", "Отмена", isDestructive: true);

        if (!confirm) return;

        try
        {
            _userRequestedStop = true;
            if (!_runningGameProcess.HasExited)
            {
                _runningGameProcess.Kill(entireProcessTree: true);
            }
            StatusText.Text = "Сборка Minecraft была принудительно остановлена.";
            ProgressText.Text = "ОСТАНОВЛЕНО";
        }
        catch (Exception ex)
        {
            ConfirmDialog.ShowError(this, "Ошибка остановки", $"Не удалось остановить процесс: {ex.Message}");
        }
        finally
        {
            _runningGameProcess = null;
            UpdateRunningStateUI();
        }
    }

    private (bool isModCrash, string explanation, string logContent) AnalyzeCrash(string instanceRoot, string version, string loader)
    {
        string? logContent = null;
        string explanation = "Игра завершилась с ошибкой. Проверьте лог сбоя ниже.";

        var crashDir = Path.Combine(instanceRoot, "crash-reports");
        if (Directory.Exists(crashDir))
        {
            var crashFiles = Directory.GetFiles(crashDir, "crash-*.txt")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            if (crashFiles.Count > 0 && (DateTime.UtcNow - crashFiles[0].LastWriteTimeUtc).TotalMinutes < 30)
            {
                try
                {
                    logContent = File.ReadAllText(crashFiles[0].FullName);
                }
                catch { }
            }
        }

        if (string.IsNullOrWhiteSpace(logContent))
        {
            var latestLog = Path.Combine(instanceRoot, "logs", "latest.log");
            if (File.Exists(latestLog))
            {
                try
                {
                    using var fs = new FileStream(latestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var len = fs.Length;
                    var offset = Math.Max(0, len - 40960);
                    fs.Seek(offset, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs);
                    logContent = reader.ReadToEnd();
                }
                catch { }
            }
        }

        if (string.IsNullOrWhiteSpace(logContent))
        {
            logContent = string.Join("\n", _minecraft.Logs.GetLogs().TakeLast(100));
        }

        if (string.IsNullOrWhiteSpace(logContent))
        {
            return (false, "Логи сбоя пусты или недоступны.", "Логи отсутствуют.");
        }

        bool isModCrash = false;

        if (logContent.Contains("net.fabricmc.loader.impl.FormattedException") ||
            logContent.Contains("ModResolutionException") ||
            logContent.Contains("Incompatible mod set") ||
            logContent.Contains("Incompatible mods found"))
        {
            isModCrash = true;
            explanation = "Ошибка загрузчика Fabric: обнаружены несовместимые версии модов или конфликт зависимостей.";
        }
        else if (logContent.Contains("requires") && (logContent.Contains("fabric-api") || logContent.Contains("fabric")))
        {
            isModCrash = true;
            explanation = "Для работы установленных модов требуется библиотека Fabric API. Функция «Починить сборку» автоматически скачает совместимый Fabric API с Modrinth.";
        }
        else if (logContent.Contains("DuplicateModsException"))
        {
            isModCrash = true;
            explanation = "Обнаружены дубликаты файлов одного и того же мода в папке mods. Починка изолирует повреждённые копии.";
        }
        else if (logContent.Contains("org.spongepowered.asm.mixin") || logContent.Contains("MixinTransformationException"))
        {
            isModCrash = true;
            explanation = "Критический сбой Mixin: один из модов попытался модифицировать внутренний код игры и вызвал конфликт.";
        }
        else if (logContent.Contains("java.lang.OutOfMemoryError"))
        {
            isModCrash = true;
            explanation = "Нехватка оперативной памяти Java при инициализации модов. Увеличьте ползунок выделения ОЗУ в настройках.";
        }
        else if (loader.Equals("fabric", StringComparison.OrdinalIgnoreCase) ||
                 loader.Equals("forge", StringComparison.OrdinalIgnoreCase) ||
                 loader.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
        {
            if (logContent.Contains("Exception") || logContent.Contains("Error") || logContent.Contains("CRASH"))
            {
                isModCrash = true;
                explanation = $"Произошла ошибка при загрузке модификаций ({loader}). Нажмите «Починить сборку» для автоматического устранения проблемы.";
            }
        }

        return (isModCrash, explanation, logContent);
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (VersionBox.SelectedItem is not MinecraftVersion selectedVersion) return;
        var version = selectedVersion.Id;
        var loader = (GameLoaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "vanilla";
        await LaunchGameCoreAsync(version, loader, _activeCustomInstancePath, null, 25565);
    }

    private async Task LaunchGameCoreAsync(string version, string loader, string? customInstanceRoot, string? serverIp, int serverPort)
    {
        var username = OfflineNameBox.Text.Trim();
        _ = SendTelemetryAsync("game_start", version, loader);
        var ramMb = (int)RamSlider.Value * 1024;

        if (ramMb > JvmTuning.RecommendedHeapMb + 1024)
        {
            var answer = ConfirmDialog.Show(this, "Проверка памяти", "Выделено почти столько же памяти, сколько рекомендует система. Это может замедлить Windows. Продолжить?", "Продолжить", "Отмена");
            if (!answer) return;
        }

        PlayButton.IsEnabled = false;
        QuickRepairButton.Visibility = Visibility.Collapsed;
        InstallProgress.Value = 0;
        ProgressText.Text = "УСТАНОВКА";
        StatusText.Text = string.IsNullOrWhiteSpace(serverIp)
            ? $"Проверяю файлы Minecraft {version} ({loader}). При первом запуске потребуется скачать игровые файлы."
            : $"Подготовка к входу на {serverIp}:{serverPort} ({version} • {loader})...";
        _userRequestedStop = false;

        try
        {
            var session = _activeMicrosoftSession;
            string? authlibInjectorPath = null;
            if (session is null && _activeElySession is not null)
            {
                session = _activeElySession;
                authlibInjectorPath = await _authlibInjector.EnsureInstalledAsync();
                SidebarProfile.Text = $"{session.Username} • Ely.by";
            }
            if (session is null)
            {
                await _profiles.SaveAsync(username);
                session = MSession.CreateOfflineSession(username);
                SidebarProfile.Text = $"{username} • Offline";
            }
            else if (_activeMicrosoftSession is not null)
            {
                SidebarProfile.Text = $"{session.Username} • Microsoft";
            }

            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = 0;
            var process = await _minecraft.LaunchAsync(
                version: version,
                loader: loader,
                session: session,
                ramMb: ramMb,
                stringDeduplication: StringDedupBox.IsChecked == true,
                authlibInjectorPath: authlibInjectorPath,
                customInstanceRoot: customInstanceRoot,
                gameLanguage: _gameLanguage,
                javaPath: _selectedJavaPath,
                onProcessExited: exitCode =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _runningGameProcess = null;
                        UpdateRunningStateUI();

                        if (_userRequestedStop)
                        {
                            StatusText.Text = "Сборка Minecraft была остановлена.";
                            ProgressText.Text = "ОСТАНОВЛЕНО";
                            return;
                        }

                        if (exitCode != 0)
                        {
                            var instanceRoot = customInstanceRoot ?? _minecraft.GetInstanceRoot(version, loader);
                            var (isModCrash, explanation, logContent) = AnalyzeCrash(instanceRoot, version, loader);

                            if (isModCrash)
                            {
                                _lastCrashReport = logContent;
                                _lastCrashExplanation = explanation;
                                _lastCrashInstancePath = instanceRoot;
                                _lastCrashVersion = version;
                                _lastCrashLoader = loader;
                                _lastCrashInstanceName = customInstanceRoot != null ? Path.GetFileName(customInstanceRoot) : $"{version} ({loader})";

                                QuickRepairButton.Visibility = Visibility.Visible;
                                ProgressText.Text = "СБОЙ МОДОВ";
                                StatusText.Text = $"Обнаружен сбой при запуске модов (код {exitCode})! Нажмите «Починить сборку» для решения.";
                            }
                            else
                            {
                                QuickRepairButton.Visibility = Visibility.Collapsed;
                                ProgressText.Text = "ЗАВЕРШЕНО";
                                StatusText.Text = $"Игра завершилась с кодом {exitCode}.";
                            }
                        }
                        else
                        {
                            QuickRepairButton.Visibility = Visibility.Collapsed;
                            ProgressText.Text = "ГОТОВ";
                            StatusText.Text = "Minecraft успешно завершил работу.";
                        }
                        LoadLibraryInstances();
                    });
                },
                onDownloadProgress: (status, percent) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = status;
                        InstallProgress.IsIndeterminate = false;
                        InstallProgress.Value = percent;
                        ProgressText.Text = $"{percent:F0}%";
                    });
                },
                serverIp: serverIp,
                serverPort: serverPort
            );

            _runningGameProcess = process;
            _runningInstancePath = customInstanceRoot ?? _minecraft.GetInstanceRoot(version, loader);
            UpdateRunningStateUI();

            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = 100;
            ProgressText.Text = "ЗАПУЩЕНО";
            StatusText.Text = string.IsNullOrWhiteSpace(serverIp)
                ? $"Minecraft {version} ({loader}) успешно запущен (PID {process.Id})!"
                : $"Minecraft {version} ({loader}) запущен — подключение к {serverIp}:{serverPort}!";
            LoadLibraryInstances();
        }
        catch (Exception ex)
        {
            InstallProgress.IsIndeterminate = false;
            ProgressText.Text = "ОШИБКА";
            StatusText.Text = $"Ошибка запуска: {ex.Message}";
            _runningGameProcess = null;
            UpdateRunningStateUI();

            var instanceRoot = customInstanceRoot ?? _minecraft.GetInstanceRoot(version, loader);
            var (isModCrash, explanation, logContent) = AnalyzeCrash(instanceRoot, version, loader);

            if (isModCrash || ex.Message.Contains("mod", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("fabric", StringComparison.OrdinalIgnoreCase))
            {
                _lastCrashReport = !string.IsNullOrWhiteSpace(logContent) ? logContent : ex.ToString();
                _lastCrashExplanation = !string.IsNullOrWhiteSpace(explanation) ? explanation : ex.Message;
                _lastCrashInstancePath = instanceRoot;
                _lastCrashVersion = version;
                _lastCrashLoader = loader;
                _lastCrashInstanceName = customInstanceRoot != null ? Path.GetFileName(customInstanceRoot) : $"{version} ({loader})";
                QuickRepairButton.Visibility = Visibility.Visible;
            }
            else
            {
                QuickRepairButton.Visibility = Visibility.Collapsed;
            }

            ConfirmDialog.ShowError(this, "Не удалось запустить Minecraft", $"{ex.Message}\n\n{(QuickRepairButton.Visibility == Visibility.Visible ? "Вы можете воспользоваться кнопкой «Починить сборку» для восстановления файлов." : "")}");
        }
        finally
        {
            UpdateRunningStateUI();
        }
    }

    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        var logWin = new LogWindow(_minecraft.Logs) { Owner = this };
        logWin.Show();
    }

    private void GameLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressSettingsWrites) return;
        _gameLanguage = (GameLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ru";
        _ = SaveSettingsAsync(_backgroundPath);
    }

    // ==========================================
    // БИБЛИОТЕКА И УПРАВЛЕНИЕ СБОРКАМИ (С добавлением модов и зависимостей)
    // ==========================================

    private void LoadLibraryInstances()
    {
        try
        {
            var instances = _minecraft.Instances.GetInstances();
            LibraryInstancesList.ItemsSource = instances;
        }
        catch { }
    }

    private void RefreshLibrary_Click(object sender, RoutedEventArgs e)
    {
        LoadLibraryInstances();
        StatusText.Text = "Список сборок в библиотеке обновлён.";
    }

    private async void CreateInstance_Click(object sender, RoutedEventArgs e)
    {
        var name = NewInstanceNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ConfirmDialog.ShowWarning(this, "Создание сборки", "Укажите название новой версии.");
            return;
        }

        if (NewInstanceVersionBox.SelectedItem is not MinecraftVersion version)
        {
            ConfirmDialog.ShowWarning(this, "Создание сборки", "Выберите версию Minecraft.");
            return;
        }

        var loader = (NewInstanceLoaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "fabric";

        CreateInstanceButton.IsEnabled = false;
        InstallProgress.IsIndeterminate = true;
        ProgressText.Text = "СОЗДАНИЕ";
        StatusText.Text = $"Создаю сборку «{name}» ({version.Id} · {loader})...";

        try
        {
            var instance = _minecraft.Instances.CreateInstance(name, version.Id, loader);

            // Список слагов популярных модов, выбранных пользователем
            var modsToInstall = new List<(string Slug, string Title)>();
            if (ChkModFabricApi.IsChecked == true && loader is "fabric" or "quilt")
                modsToInstall.Add(("fabric-api", "Fabric API"));
            if (ChkModSodium.IsChecked == true && loader is "fabric" or "quilt")
                modsToInstall.Add(("sodium", "Sodium"));
            if (ChkModIris.IsChecked == true && loader is "fabric" or "quilt")
                modsToInstall.Add(("iris", "Iris Shaders"));
            if (ChkModFerriteCore.IsChecked == true)
                modsToInstall.Add(("ferrite-core", "FerriteCore"));
            if (ChkModJei.IsChecked == true)
                modsToInstall.Add(("jei", "Just Enough Items"));

            int installedMods = 0;
            int installedDeps = 0;

            foreach (var (slug, title) in modsToInstall)
            {
                StatusText.Text = $"Скачиваю {title} и все обязательные зависимости...";
                try
                {
                    var search = await _modrinth.SearchAsync(slug, "mod", loader, version.Id);
                    var project = search.FirstOrDefault(x => x.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase)) ?? search.FirstOrDefault();
                    if (project != null)
                    {
                        var result = await _modrinth.InstallAsync(project, "mod", loader, version.Id, instance.FolderPath);
                        installedMods++;
                        installedDeps += result.Dependencies.Count;
                    }
                }
                catch { }
            }

            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = 100;
            ProgressText.Text = "ГОТОВО";
            LoadLibraryInstances();

            var summary = $"Сборка «{instance.Name}» успешно создана!\nПапка: {instance.FolderPath}";
            if (installedMods > 0)
            {
                summary += $"\n\nУстановлено основных модов: {installedMods}\nАвтоматически загружено зависимостей: {installedDeps}";
            }
            StatusText.Text = summary.Replace("\n", " ");
            ConfirmDialog.ShowSuccess(this, "Сборка создана", summary);
        }
        catch (Exception ex)
        {
            InstallProgress.IsIndeterminate = false;
            ProgressText.Text = "ОШИБКА";
            ConfirmDialog.ShowError(this, "Ошибка создания сборки", $"Не удалось создать сборку: {ex.Message}");
        }
        finally
        {
            CreateInstanceButton.IsEnabled = true;
        }
    }

    private void LaunchLibraryInstance_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameInstanceInfo instance) return;
        _activeCustomInstancePath = instance.FolderPath;

        foreach (MinecraftVersion v in VersionBox.Items)
        {
            if (v.Id.Equals(instance.Version, StringComparison.OrdinalIgnoreCase))
            {
                VersionBox.SelectedItem = v;
                break;
            }
        }

        for (int i = 0; i < GameLoaderBox.Items.Count; i++)
        {
            if ((GameLoaderBox.Items[i] as ComboBoxItem)?.Tag?.ToString() == instance.Loader)
            {
                GameLoaderBox.SelectedIndex = i;
                break;
            }
        }

        ProfileSummaryText.Text = $"{instance.Name} ({instance.Loader} · {instance.Version})";
        NavigateTo(0);
        Play_Click(sender, e);
    }

    private async void RepairInstance_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameInstanceInfo instance) return;
        var (_, explanation, logContent) = AnalyzeCrash(instance.FolderPath, instance.Version, instance.Loader);
        var dialog = new RepairDialog(instance.Name, explanation, logContent) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await RunRepairWorkflowAsync(instance.FolderPath, instance.Version, instance.Loader, instance.Name);
        }
    }

    private async void QuickRepair_Click(object sender, RoutedEventArgs e)
    {
        var version = _lastCrashVersion ?? (VersionBox.SelectedItem as MinecraftVersion)?.Id ?? "1.20.4";
        var loader = _lastCrashLoader ?? (GameLoaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "vanilla";
        var path = _lastCrashInstancePath ?? _activeCustomInstancePath ?? _minecraft.GetInstanceRoot(version, loader);
        var name = _lastCrashInstanceName ?? "Текущая сборка";
        var explanation = _lastCrashExplanation ?? "Обнаружен сбой при загрузке модификаций.";
        var log = _lastCrashReport ?? string.Join("\n", _minecraft.Logs.GetLogs().TakeLast(60));

        var dialog = new RepairDialog(name, explanation, log) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await RunRepairWorkflowAsync(path, version, loader, name);
        }
    }

    private async Task RunRepairWorkflowAsync(string instanceRoot, string version, string loader, string instanceName)
    {
        PlayButton.IsEnabled = false;
        InstallProgress.IsIndeterminate = true;
        ProgressText.Text = "ПОЧИНКА";
        StatusText.Text = $"Восстанавливаю сборку {instanceName}…";

        try
        {
            var report = await _minecraft.RepairInstanceAsync(instanceRoot, version, loader, progress =>
            {
                Dispatcher.Invoke(() => StatusText.Text = progress);
            });

            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = 100;
            ProgressText.Text = "ПОЧИНЕНО";
            StatusText.Text = $"Сборка «{instanceName}» успешно починена и готова к запуску!";
            QuickRepairButton.Visibility = Visibility.Collapsed;
            _lastCrashReport = null;
            _lastCrashExplanation = null;
            LoadLibraryInstances();
            ConfirmDialog.ShowSuccess(this, "Восстановление завершено", report);
        }
        catch (Exception ex)
        {
            InstallProgress.IsIndeterminate = false;
            ProgressText.Text = "ОШИБКА";
            StatusText.Text = $"Ошибка починки: {ex.Message}";
            ConfirmDialog.ShowError(this, "Ошибка восстановления", $"Не удалось завершить восстановление: {ex.Message}");
        }
        finally
        {
            PlayButton.IsEnabled = true;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;
        UpdateStatusText.Text = "Подключение к серверу обновлений...";

        try
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            {
                UpdateStatusText.Text = "Не удалось определить путь к исполняемому файлу лаунчера.";
                return;
            }

            UpdateStatusText.Text = "Загружаю обновление лаунчера с сервера...";
            string[] updateMirrors =
            [
                "https://kslauncher.pages.dev/KSLauncher.exe",
                "https://k1nexis.github.io/KSLauncher/KSLauncher.exe",
                "https://kslauncher.github.io/KSLauncher.exe"
            ];
            var tempExe = Path.Combine(Path.GetTempPath(), $"KSLauncher_new_{Guid.NewGuid():N}.exe");

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.Add("User-Agent", "KSLauncher/0.2.0");

            System.Net.Http.HttpResponseMessage? response = null;
            Exception? lastMirrorError = null;
            foreach (var mirrorUrl in updateMirrors)
            {
                try
                {
                    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    var candidate = await http.GetAsync(mirrorUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
                    if (candidate.IsSuccessStatusCode)
                    {
                        response = candidate;
                        UpdateStatusText.Text = $"Скачиваю с зеркала: {new Uri(mirrorUrl).Host}...";
                        break;
                    }
                    candidate.Dispose();
                }
                catch (Exception mirrorEx)
                {
                    lastMirrorError = mirrorEx;
                }
            }

            if (response == null)
            {
                throw new InvalidOperationException($"Все зеркала обновлений недоступны ({lastMirrorError?.Message ?? "таймаут"}).");
            }

            using var activeResponse = response;
            var totalBytes = activeResponse.Content.Headers.ContentLength ?? -1L;
            using var source = await activeResponse.Content.ReadAsStreamAsync();
            using var target = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                if (totalBytes > 0)
                {
                    var pct = (double)totalRead / totalBytes * 100.0;
                    Dispatcher.Invoke(() =>
                    {
                        UpdateProgressBar.IsIndeterminate = false;
                        UpdateProgressBar.Value = pct;
                        UpdateStatusText.Text = $"Загружено {pct:F0}% ({totalRead / (1024.0 * 1024.0):F1} МБ из {totalBytes / (1024.0 * 1024.0):F1} МБ)...";
                    });
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        UpdateProgressBar.IsIndeterminate = true;
                        UpdateStatusText.Text = $"Загружено {totalRead / (1024.0 * 1024.0):F1} МБ...";
                    });
                }
            }

            target.Close();

            var fileInfo = new FileInfo(tempExe);
            if (fileInfo.Length < 500_000)
            {
                throw new InvalidOperationException("Загруженный файл обновления повреждён или неполный.");
            }

            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = 100;
            UpdateStatusText.Text = "Обновление успешно скачано!";

            var answer = ConfirmDialog.Show(this, "Обновление готово",
                "Новая версия лаунчера успешно загружена!\nЛаунчер сейчас перезапустится для завершения обновления.",
                "Обновить и перезапустить", "Отмена");

            if (!answer)
            {
                try { File.Delete(tempExe); } catch { }
                UpdateStatusText.Text = "Обновление отложено пользователем.";
                return;
            }

            var currentPid = Environment.ProcessId;
            var updaterBat = Path.Combine(Path.GetTempPath(), $"ks_updater_{currentPid}.bat");
            var batScript = $@"@echo off
chcp 65001 >nul
timeout /t 1 /nobreak >nul
:waitloop
tasklist /fi ""PID eq {currentPid}"" 2>nul | findstr /i ""{currentPid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto waitloop
)
copy /y ""{tempExe}"" ""{currentExe}"" >nul
del ""{tempExe}"" >nul 2>&1
start """" ""{currentExe}""
del ""%~f0"" >nul 2>&1
";
            File.WriteAllText(updaterBat, batScript, new System.Text.UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = updaterBat,
                CreateNoWindow = true,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = $"Ошибка обновления: {ex.Message}";
            ConfirmDialog.ShowError(this, "Ошибка обновления", $"Не удалось загрузить или применить обновление:\n{ex.Message}");
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task CheckStartupUpdateAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Add("User-Agent", "KSLauncher/0.2.0");
            var res = await http.GetStringAsync(_elyWorkerUrl.TrimEnd('/') + "/v1/content/home");
            using var doc = JsonDocument.Parse(res);
            if (doc.RootElement.TryGetProperty("updateNotice", out var noticeEl) && noticeEl.ValueKind == JsonValueKind.Object)
            {
                if (noticeEl.TryGetProperty("available", out var avail) && avail.GetBoolean())
                {
                    Dispatcher.Invoke(() =>
                    {
                        var msg = noticeEl.TryGetProperty("message", out var m)
                            ? m.GetString()
                            : "Обновление доступно. Нажмите кнопку «Обновить» в настройках.";

                        var answer = ConfirmDialog.Show(this, "Обновление доступно",
                            $"{msg}\n\nОткрыть настройки сейчас?",
                            "В настройки", "Позже");

                        if (answer)
                        {
                            NavigateTo(2);
                        }
                    });
                }
            }
        }
        catch { }
    }

    private void OpenInstanceFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameInstanceInfo instance) return;
        if (Directory.Exists(instance.FolderPath))
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = instance.FolderPath, UseShellExecute = true });
        }
    }

    private void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameInstanceInfo instance) return;
        var confirmed = ConfirmDialog.Show(this, "Удаление сборки", $"Удалить сборку «{instance.Name}»?\nВсе моды и файлы этой сборки будут безвозвратно удалены.", "Удалить", "Отмена", isDestructive: true);
        if (confirmed)
        {
            try
            {
                _minecraft.Instances.DeleteInstance(instance.FolderPath);
                LoadLibraryInstances();
                StatusText.Text = $"Сборка «{instance.Name}» удалена.";
            }
            catch (Exception ex)
            {
                ConfirmDialog.ShowInfo(this, "Ошибка удаления", $"Не удалось удалить сборку: {ex.Message}");
            }
        }
    }

    // ==========================================
    // КАТАЛОГ МОДОВ (Modrinth & CurseForge, Enter-search, Styling)
    // ==========================================

    private void ModSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SearchMods_Click(sender, e);
        }
    }

    private void ModSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        var platform = (ModSourceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "modrinth";
        StatusText.Text = $"Выбран источник модов: {(platform == "curseforge" ? "CurseForge" : "Modrinth")}. Нажмите Enter или «Найти моды».";
    }

    private async void SearchMods_Click(object sender, RoutedEventArgs e)
    {
        var platform = (ModSourceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "modrinth";
        var selectedType = ProjectTypeBox.SelectedItem as ComboBoxItem;
        var projectType = selectedType?.Tag?.ToString() ?? "mod";
        var selectedLoader = ModLoaderBox.SelectedItem as ComboBoxItem;
        var loader = selectedLoader?.Tag?.ToString() ?? "fabric";
        var loaderTitle = selectedLoader?.Content?.ToString() ?? "Fabric";
        var gameVersion = (ModVersionBox.SelectedItem as MinecraftVersion)?.Id ?? "";
        var versionTitle = string.IsNullOrWhiteSpace(gameVersion) ? "Любая" : gameVersion;
        var query = ModSearchBox.Text.Trim();

        SearchModsButton.IsEnabled = false;
        try
        {
            StatusText.Text = string.IsNullOrWhiteSpace(query)
                ? $"Загрузка популярных модов из {(platform == "curseforge" ? "CurseForge" : "Modrinth")}…"
                : $"Поиск в {(platform == "curseforge" ? "CurseForge" : "Modrinth")}…";
            var unifiedList = new List<UnifiedModItem>();

            if (platform == "curseforge")
            {
                var cfResults = await _curseforge.SearchAsync(query, projectType, loader, gameVersion);
                foreach (var cf in cfResults)
                {
                    unifiedList.Add(new UnifiedModItem(
                        Id: cf.Id.ToString(),
                        Slug: cf.Slug,
                        Title: cf.Title,
                        Description: string.IsNullOrWhiteSpace(cf.Description) ? "Описание отсутствует." : cf.Description,
                        Downloads: cf.Downloads,
                        ProjectType: cf.ProjectType,
                        Platform: "CurseForge",
                        PlatformColor: "#FF6B00",
                        RawProject: cf,
                        Loader: loaderTitle,
                        Version: versionTitle
                    ));
                }
            }
            else
            {
                var mrResults = await _modrinth.SearchAsync(query, projectType, loader, gameVersion);
                foreach (var mr in mrResults)
                {
                    unifiedList.Add(new UnifiedModItem(
                        Id: mr.ProjectId,
                        Slug: mr.Slug,
                        Title: mr.Title,
                        Description: string.IsNullOrWhiteSpace(mr.Description) ? "Описание отсутствует." : mr.Description,
                        Downloads: mr.Downloads,
                        ProjectType: mr.ProjectType,
                        Platform: "Modrinth",
                        PlatformColor: "#1BD96A",
                        RawProject: mr,
                        Loader: loaderTitle,
                        Version: versionTitle
                    ));
                }
            }

            ModsList.ItemsSource = unifiedList;
            StatusText.Text = $"Найдено проектов в {platform}: {unifiedList.Count}. Нажмите иконку ⬇ для скачивания в лаунчере.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Каталог {platform} недоступен: {ex.Message}";
        }
        finally
        {
            SearchModsButton.IsEnabled = true;
        }
    }

    private void ModLoaderTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string loaderText)
        {
            foreach (var item in ModLoaderBox.Items)
            {
                if (item is ComboBoxItem cbi &&
                    (string.Equals(cbi.Content?.ToString(), loaderText, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(cbi.Tag?.ToString(), loaderText, StringComparison.OrdinalIgnoreCase)))
                {
                    ModLoaderBox.SelectedItem = cbi;
                    SearchMods_Click(sender, e);
                    break;
                }
            }
        }
    }

    private void ModVersionTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string versionText && versionText != "Любая")
        {
            foreach (var item in ModVersionBox.Items)
            {
                if (item is MinecraftVersion ver && string.Equals(ver.Id, versionText, StringComparison.OrdinalIgnoreCase))
                {
                    ModVersionBox.SelectedItem = ver;
                    SearchMods_Click(sender, e);
                    break;
                }
            }
        }
    }

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UnifiedModItem item) return;
        var type = (ProjectTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mod";
        var loader = (ModLoaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "fabric";
        var version = (ModVersionBox.SelectedItem as MinecraftVersion)?.Id;
        if (version is null) { StatusText.Text = "Выберите версию Minecraft."; return; }

        var targetInstanceRoot = _activeCustomInstancePath ?? _minecraft.InstancesRoot;

        try
        {
            StatusText.Text = $"Загружаю «{item.Title}» из {item.Platform} вместе с зависимостями…";

            string fileName;
            string targetDir;
            IReadOnlyList<string> dependencies = [];

            if (item.Platform == "CurseForge" && item.RawProject is CurseForgeProject cfProject)
            {
                var result = await _curseforge.InstallAsync(cfProject, type, loader, version, targetInstanceRoot);
                fileName = result.FileName;
                targetDir = result.TargetDirectory;
                dependencies = result.Dependencies;
            }
            else if (item.RawProject is ModrinthProject mrProject)
            {
                var result = await _modrinth.InstallAsync(mrProject, type, loader, version, targetInstanceRoot);
                fileName = result.FileName;
                targetDir = result.TargetDirectory;
                dependencies = result.Dependencies;
            }
            else
            {
                throw new InvalidOperationException("Неизвестный тип проекта.");
            }

            if (type == "mod") GameLoaderBox.SelectedIndex = ModLoaderBox.SelectedIndex;
            StatusText.Text = dependencies.Count > 0
                ? $"Установлено: {fileName}. Автоматически загружено зависимостей: {dependencies.Count}!"
                : $"Установлено в {targetDir}: {fileName}.";

            LoadLibraryInstances();

            var message = $"Файл: {fileName}\nПапка: {targetDir}";
            if (dependencies.Count > 0)
            {
                message += $"\n\nАвтоматически загруженные зависимости:\n• {string.Join("\n• ", dependencies)}";
            }
            ConfirmDialog.ShowSuccess(this, "Загрузка завершена", message);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Загрузка не выполнена: {ex.Message}";
            ConfirmDialog.ShowError(this, "Ошибка загрузки", ex.Message);
        }
    }

    // ==========================================
    // УПРАВЛЕНИЕ JAVA RUNTIME (JavaService)
    // ==========================================

    private void LoadJavaRuntimes()
    {
        try
        {
            var javas = _minecraft.Java.DetectInstalledJavas();
            JavaRuntimeBox.ItemsSource = javas;
            JavaRuntimeBox.SelectedIndex = 0;

            if (!string.IsNullOrWhiteSpace(_selectedJavaPath))
            {
                JavaStatusText.Text = $"Пользовательский путь: {_selectedJavaPath}";
            }
        }
        catch { }
    }

    private void JavaRuntimeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || JavaRuntimeBox.SelectedItem is not JavaRuntimeInfo info) return;
        _selectedJavaPath = info.IsAuto ? null : info.Path;
        JavaStatusText.Text = info.IsAuto ? "Автоматический выбор (система подберёт подходящую Java)." : $"Используется: {info.Path}";
        _ = SaveSettingsAsync(_backgroundPath);
    }

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите javaw.exe",
            Filter = "Исполняемый файл Java|javaw.exe;java.exe|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            _selectedJavaPath = dialog.FileName;
            var ver = JavaService.ProbeJavaVersion(_selectedJavaPath);
            JavaStatusText.Text = $"Выбрана вручную: {_selectedJavaPath} (Java {ver})";
            _ = SaveSettingsAsync(_backgroundPath);
        }
    }

    // ==========================================
    // СОХРАНЁННЫЕ АККАУНТЫ (AccountManager)
    // ==========================================

    private void SidebarProfile_Click(object sender, RoutedEventArgs e) => NavigateTo(3);

    private void SavedAccountItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SavedAccount account && !account.IsActive)
        {
            ApplyActiveAccount(account);
            StatusText.Text = $"Активный профиль изменён на: {account.DisplayName}.";
        }
    }

    private void SavedAccountCard_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is SavedAccount account && !account.IsActive)
        {
            ApplyActiveAccount(account);
            StatusText.Text = $"Активный профиль изменён на: {account.DisplayName}.";
        }
    }

    private void SidebarAccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressAccountSync) return;
        if (SidebarAccountBox.SelectedItem is SavedAccount account)
        {
            ApplyActiveAccount(account);
            StatusText.Text = $"Активный профиль изменён на: {account.DisplayName}.";
        }
    }

    private void SelectSavedAccount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SavedAccount account) return;
        ApplyActiveAccount(account);
        StatusText.Text = $"Активный профиль изменён на: {account.DisplayName}.";
    }

    private void RemoveSavedAccount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SavedAccount account) return;
        _accountManager.RemoveAccount(account.Id);
        LoadSavedAccounts();
        StatusText.Text = $"Аккаунт {account.DisplayName} удалён.";
    }

    private void ApplyActiveAccount(SavedAccount account)
    {
        _suppressAccountSync = true;
        try
        {
            _accountManager.SetActiveAccount(account.Id);
            SidebarProfile.Text = $"{account.DisplayName} • {account.Type.ToUpperInvariant()}";
            if (SidebarProfileSubtext is not null)
            {
                SidebarProfileSubtext.Text = account.Type.ToLowerInvariant() switch
                {
                    "ely" => "Скины Ely.by + Authlib",
                    "ks" => "Скины KS Cloud",
                    "microsoft" => "Лицензия Mojang",
                    _ => "Локальная сессия"
                };
            }

            if (account.Type is "offline" or "ks")
            {
                OfflineNameBox.Text = account.Username;
                _activeMicrosoftSession = null;
                _activeElySession = null;
            }
            else if (account.Type == "ely" && !string.IsNullOrWhiteSpace(account.AccessToken))
            {
                _activeElySession = new MSession(account.Username, account.Uuid ?? "", account.AccessToken) { UserType = "ely" };
                _activeMicrosoftSession = null;
                OfflineNameBox.Text = account.Username;
            }
            else if (account.Type == "microsoft" && !string.IsNullOrWhiteSpace(account.AccessToken))
            {
                _activeMicrosoftSession = new MSession(account.Username, account.Uuid ?? "", account.AccessToken);
                _activeElySession = null;
                OfflineNameBox.Text = account.Username;
            }

            var accounts = _accountManager.GetAccounts();
            SavedAccountsList.ItemsSource = null;
            SavedAccountsList.ItemsSource = accounts;

            if (SidebarAccountBox is not null)
            {
                SidebarAccountBox.ItemsSource = null;
                SidebarAccountBox.ItemsSource = accounts;
                SidebarAccountBox.SelectedItem = accounts.FirstOrDefault(x => x.Id.Equals(account.Id, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _suppressAccountSync = false;
        }
    }

    private void LoadSavedAccounts()
    {
        try
        {
            var accounts = _accountManager.GetAccounts();
            var active = _accountManager.GetActiveAccount();

            if (active is not null)
            {
                ApplyActiveAccount(active);
            }
            else
            {
                _suppressAccountSync = true;
                try
                {
                    SavedAccountsList.ItemsSource = null;
                    SavedAccountsList.ItemsSource = accounts;
                    if (SidebarAccountBox is not null)
                    {
                        SidebarAccountBox.ItemsSource = null;
                        SidebarAccountBox.ItemsSource = accounts;
                    }
                }
                finally
                {
                    _suppressAccountSync = false;
                }
            }
        }
        catch { }
    }

    // ==========================================
    // ФОН И АДАПТИВНЫЕ ЦВЕТА
    // ==========================================

    private async void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Фон KS Launcher",
            Filter = "Изображения и видео|*.png;*.jpg;*.jpeg;*.bmp;*.mp4|Изображения|*.png;*.jpg;*.jpeg;*.bmp|Видео MP4|*.mp4",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ApplyBackground(dialog.FileName);
            await SaveSettingsAsync(dialog.FileName);
        }
        catch (Exception ex) { BackgroundStatusText.Text = $"Не удалось установить фон: {ex.Message}"; }
    }

    private void ApplyBackground(string path)
    {
        _backgroundPath = path;
        var ext = Path.GetExtension(path);

        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            VideoBackground.Source = new Uri(path);
            VideoBackground.Visibility = Visibility.Visible;
            VideoBackground.Play();

            var companion = Path.ChangeExtension(path, ".jpg");
            if (!File.Exists(companion)) companion = Path.ChangeExtension(path, ".png");

            if (File.Exists(companion))
            {
                var bitmap = LoadBitmapSafely(companion);
                if (bitmap != null)
                {
                    ImageBackground.Source = bitmap;
                    ImageBackground.Visibility = Visibility.Visible;
                    AdaptThemeColorsFromImage(bitmap);
                }
            }
            else if (ImageBackground.Source is BitmapSource existingBitmap)
            {
                AdaptThemeColorsFromImage(existingBitmap);
            }
            BackgroundStatusText.Text = $"{Path.GetFileName(path)} (Видео + адаптированные цвета)";
        }
        else
        {
            VideoBackground.Stop();
            VideoBackground.Source = null;
            VideoBackground.Visibility = Visibility.Collapsed;

            var bitmap = LoadBitmapSafely(path);
            if (bitmap != null)
            {
                ImageBackground.Source = bitmap;
                ImageBackground.Visibility = Visibility.Visible;
                AdaptThemeColorsFromImage(bitmap);
            }
            BackgroundStatusText.Text = $"{Path.GetFileName(path)} (Цвета темы адаптированы)";
        }
    }

    private static BitmapImage? LoadBitmapSafely(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            return image;
        }
        catch { return null; }
    }

    private void AdaptThemeColorsFromImage(BitmapSource bitmap)
    {
        try
        {
            var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int width = formatted.PixelWidth;
            int height = formatted.PixelHeight;
            if (width <= 0 || height <= 0) return;

            // Сэмплируем до ~10 000 точек равномерно по изображению
            int stepX = Math.Max(1, width / 100);
            int stepY = Math.Max(1, height / 100);
            var stride = width * 4;
            var pixels = new byte[stride * height];
            formatted.CopyPixels(pixels, stride, 0);

            // 16 секторов цветового круга по 22.5 градуса
            const int numBins = 16;
            var binScores = new double[numBins];
            var binSumH = new double[numBins];
            var binSumS = new double[numBins];
            var binSumV = new double[numBins];

            for (int y = 0; y < height; y += stepY)
            {
                for (int x = 0; x < width; x += stepX)
                {
                    int index = (y * stride) + (x * 4);
                    byte b = pixels[index];
                    byte g = pixels[index + 1];
                    byte r = pixels[index + 2];
                    var (h, s, v) = ColorToHsv(Color.FromRgb(r, g, b));

                    // Отсекаем слишком тёмные, выцветшие и блеклые серые пиксели
                    if (v < 0.18 || (v > 0.95 && s < 0.12) || s < 0.20)
                        continue;

                    // Вес насыщенности и яркости
                    double weight = Math.Pow(s, 1.4) * Math.Sin(Math.PI * Math.Clamp(v, 0.15, 0.95));
                    int bin = (int)Math.Floor(h / 22.5) % numBins;
                    if (bin < 0) bin += numBins;

                    binScores[bin] += weight;
                    binSumH[bin] += h * weight;
                    binSumS[bin] += s * weight;
                    binSumV[bin] += v * weight;
                }
            }

            // Находим главный преобладающий цвет 1
            int bestBin1 = -1;
            double maxScore1 = -1;
            for (int i = 0; i < numBins; i++)
            {
                if (binScores[i] > maxScore1)
                {
                    maxScore1 = binScores[i];
                    bestBin1 = i;
                }
            }

            if (bestBin1 < 0 || maxScore1 <= 0) return;

            double avgH1 = binSumH[bestBin1] / binScores[bestBin1];
            double avgS1 = Math.Clamp((binSumS[bestBin1] / binScores[bestBin1]) * 1.15, 0.70, 0.98);
            double avgV1 = Math.Clamp((binSumV[bestBin1] / binScores[bestBin1]) * 1.15, 0.75, 0.98);
            Color primaryColor = HsvToColor(avgH1, avgS1, avgV1);

            // Находим второй выразительный цвет 2 (на расстоянии не менее 35 градусов)
            int bestBin2 = -1;
            double maxScore2 = -1;
            for (int i = 0; i < numBins; i++)
            {
                double binCenterH = (i + 0.5) * 22.5;
                double dist = AngularDistance(binCenterH, avgH1);
                if (dist >= 35.0 && binScores[i] > maxScore2)
                {
                    maxScore2 = binScores[i];
                    bestBin2 = i;
                }
            }

            Color secondaryColor;
            if (bestBin2 >= 0 && maxScore2 > maxScore1 * 0.12)
            {
                double avgH2 = binSumH[bestBin2] / binScores[bestBin2];
                double avgS2 = Math.Clamp((binSumS[bestBin2] / binScores[bestBin2]) * 1.15, 0.70, 0.98);
                double avgV2 = Math.Clamp((binSumV[bestBin2] / binScores[bestBin2]) * 1.15, 0.75, 0.98);
                secondaryColor = HsvToColor(avgH2, avgS2, avgV2);
            }
            else
            {
                secondaryColor = HsvToColor((avgH1 + 45.0) % 360, avgS1, avgV1);
            }

            _accentColor = ToHex(primaryColor);
            _secondaryAccentColor = ToHex(secondaryColor);
            _pendingAccentColor = _accentColor;
            _pendingSecondaryAccentColor = _secondaryAccentColor;
            _gradientAccent = true;
            _pendingGradientAccent = true;
            if (GradientAccentBox != null) GradientAccentBox.IsChecked = true;
            UpdateColorWheelLayout();
            ApplyAccentTheme();
            UpdatePendingColorPreview();
            _ = SaveSettingsAsync(_backgroundPath);
        }
        catch { }
    }

    private static double AngularDistance(double h1, double h2)
    {
        var d = Math.Abs(h1 - h2) % 360;
        return d > 180 ? 360 - d : d;
    }

    private async void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        VideoBackground.Stop();
        VideoBackground.Source = null;
        VideoBackground.Visibility = Visibility.Collapsed;
        ImageBackground.Source = null;
        ImageBackground.Visibility = Visibility.Collapsed;
        BackgroundStatusText.Text = "Используется стандартный фон";
        await SaveSettingsAsync(null);
    }

    private void VideoBackground_MediaEnded(object sender, RoutedEventArgs e)
    {
        VideoBackground.Position = TimeSpan.Zero;
        VideoBackground.Play();
    }

    // ==========================================
    // АВТОРИЗАЦИЯ
    // ==========================================

    private async void SaveOffline_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var username = OfflineNameBox.Text.Trim();
            var profile = await _profiles.SaveAsync(username);
            var saved = new SavedAccount(
                Id: $"offline_{username}",
                Username: username,
                Type: "offline",
                Uuid: profile.Uuid,
                IsActive: true
            );
            _accountManager.SaveAccount(saved);
            ApplyActiveAccount(saved);
            StatusText.Text = $"Сохранён локальный профиль {username}.offline (UUID {profile.Uuid}).";
        }
        catch (Exception ex) { ConfirmDialog.ShowWarning(this, "Некорректный ник", ex.Message); }
    }

    private async void MicrosoftLogin_Click(object sender, RoutedEventArgs e)
    {
        MicrosoftLoginButton.IsEnabled = false;
        MicrosoftLoginButton.Content = "Ожидание входа…";
        try
        {
            _activeMicrosoftSession = await _microsoft.SignInAsync();
            var saved = new SavedAccount(
                Id: $"ms_{_activeMicrosoftSession.UUID}",
                Username: _activeMicrosoftSession.Username ?? "Player",
                Type: "microsoft",
                Uuid: _activeMicrosoftSession.UUID,
                AccessToken: _activeMicrosoftSession.AccessToken,
                IsActive: true
            );
            _accountManager.SaveAccount(saved);
            ApplyActiveAccount(saved);
            MicrosoftLoginButton.Content = "Подключено: " + _activeMicrosoftSession.Username;
            StatusText.Text = $"Официальный вход выполнен: {_activeMicrosoftSession.Username}.";
        }
        catch (Exception ex)
        {
            MicrosoftLoginButton.Content = "Войти через Microsoft";
            StatusText.Text = "Вход Microsoft не завершён.";
            ConfirmDialog.ShowError(this, "Вход Microsoft", ex.Message);
        }
        finally { MicrosoftLoginButton.IsEnabled = true; }
    }

    private async void ElyLogin_Click(object sender, RoutedEventArgs e)
    {
        ElyLoginButton.IsEnabled = false;
        ElyStatusText.Text = "Открываю безопасный вход Ely.by…";
        try
        {
            var workerUrl = ElyWorkerUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(workerUrl))
                workerUrl = _elyWorkerUrl;

            var account = await _ely.SignInAsync(workerUrl);
            var saved = new SavedAccount(
                Id: $"ely_{(!string.IsNullOrWhiteSpace(account.Uuid) ? account.Uuid : account.Username.ToLowerInvariant())}",
                Username: account.Username,
                Type: "ely",
                Uuid: account.Uuid,
                AccessToken: account.AccessToken,
                ExpiresAt: account.ExpiresAt,
                IsActive: true
            );
            _accountManager.SaveAccount(saved);
            ApplyActiveAccount(saved);

            ElyStatusText.Text = $"Подключено: {account.Username}.ely! Можно добавить ещё аккаунты.";
            ElyLoginButton.Content = "➕  Добавить ещё аккаунт Ely.by";
            StatusText.Text = $"Вход Ely.by выполнен: {account.Username}.ely.";
        }
        catch (Exception ex)
        {
            ElyStatusText.Text = $"Ошибка входа Ely.by: {ex.Message}";
            ConfirmDialog.ShowWarning(this, "Вход Ely.by", ex.Message);
        }
        finally { ElyLoginButton.IsEnabled = true; }
    }

    private async void SaveElyWorkerUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = ElyWorkerUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ElyStatusText.Text = "Укажите URL воркера Ely.by.";
            return;
        }
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ElyStatusText.Text = "URL должен начинаться с https://";
            return;
        }
        if (!url.EndsWith("/")) url += "/";
        _elyWorkerUrl = url;
        ElyWorkerUrlBox.Text = url;
        CloudUrlBox.Text = url.TrimEnd('/');
        await SaveSettingsAsync(_backgroundPath);
        ElyStatusText.Text = $"Worker URL сохранён: {url}";
    }

    private async void CloudLogin_Click(object sender, RoutedEventArgs e) => await RunCloudAuthAsync(register: false);
    private async void CloudRegister_Click(object sender, RoutedEventArgs e) => await RunCloudAuthAsync(register: true);
    private async Task RunCloudAuthAsync(bool register)
    {
        CloudStatusText.Text = register ? "Создаю аккаунт KS System…" : "Выполняю вход…";
        try
        {
            var email = CloudEmailBox.Text.Trim();
            var password = CloudPasswordBox.Password;
            var username = register ? await _cloud.RegisterAsync(CloudUrlBox.Text, email, CloudUsernameBox.Text.Trim(), password) : await _cloud.LoginAsync(CloudUrlBox.Text, email, password);
            CloudStatusText.Text = $"KS System подключена: {username}.ks.";
            CloudLogoutButton.IsEnabled = true;

            var saved = new SavedAccount(
                Id: $"ks_{username}",
                Username: username,
                Type: "ks",
                IsActive: true
            );
            _accountManager.SaveAccount(saved);
            ApplyActiveAccount(saved);
            StatusText.Text = $"Вход в KS System выполнен: {username}.ks";
        }
        catch (Exception ex) { CloudStatusText.Text = ex.Message; }
    }

    private async void CloudLogout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _cloud.LogoutAsync(CloudUrlBox.Text);
            CloudStatusText.Text = "Сессия KS System завершена.";
            CloudLogoutButton.IsEnabled = false;
        }
        catch (Exception ex) { CloudStatusText.Text = ex.Message; }
    }

    private async void UploadSkin_Click(object sender, RoutedEventArgs e) => await UploadCloudAssetAsync("skin");
    private async void UploadCape_Click(object sender, RoutedEventArgs e) => await UploadCloudAssetAsync("cape");
    private async Task UploadCloudAssetAsync(string kind)
    {
        var dialog = new OpenFileDialog { Title = kind == "skin" ? "Выберите PNG скин" : "Выберите PNG плащ", Filter = "PNG (*.png)|*.png", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await _cloud.UploadPngAsync(CloudUrlBox.Text, kind, dialog.FileName);
            CloudStatusText.Text = $"{(kind == "skin" ? "Скин" : "Плащ")} загружен в KS System.";
        }
        catch (Exception ex) { CloudStatusText.Text = ex.Message; }
    }

    // ==========================================
    // НАВИГАЦИЯ И ОФОРМЛЕНИЕ
    // ==========================================

    private void GameLoaderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (ModLoaderBox is not null && GameLoaderBox.SelectedIndex >= 0 && GameLoaderBox.SelectedIndex < ModLoaderBox.Items.Count)
            ModLoaderBox.SelectedIndex = GameLoaderBox.SelectedIndex;
        UpdateProfileSummary();
    }

    private void ModLoaderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (GameLoaderBox is not null && ModLoaderBox.SelectedIndex >= 0 && ModLoaderBox.SelectedIndex < GameLoaderBox.Items.Count)
            GameLoaderBox.SelectedIndex = ModLoaderBox.SelectedIndex;
    }

    private void GameProfile_Changed(object sender, SelectionChangedEventArgs e) => UpdateProfileSummary();
    private void UpdateProfileSummary()
    {
        if (ProfileSummaryText is null) return;
        var version = (VersionBox.SelectedItem as MinecraftVersion)?.Id ?? "Minecraft";
        var loader = (GameLoaderBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Vanilla";
        ProfileSummaryText.Text = $"{loader} · {version}";
    }

    private async void VersionFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _suppressVersionReload) return;
        await LoadVersionsAsync();
        await SaveSettingsAsync(_backgroundPath);
    }

    private void Library_Click(object sender, RoutedEventArgs e) => NavigateTo(1);
    private void Mods_Click(object sender, RoutedEventArgs e) => NavigateTo(2);
    private void Accounts_Click(object sender, RoutedEventArgs e) => NavigateTo(3);
    private void Settings_Click(object sender, RoutedEventArgs e) => NavigateTo(4);
    private void Friends_Click(object sender, RoutedEventArgs e) => NavigateTo(5);
    private void Servers_Click(object sender, RoutedEventArgs e) => NavigateTo(6);
    private void GoHome_Click(object sender, RoutedEventArgs e) => NavigateTo(0);
    private void NavigateTo(int index) => MainTabs.SelectedIndex = index;

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs) || MainTabs.SelectedItem is not TabItem item) return;
        PageTitle.Text = item.Header?.ToString() switch
        {
            "Главная" => "Обзор",
            _ => item.Header?.ToString() ?? "KS Launcher"
        };
        var buttons = new[] { NavHomeButton, NavLibraryButton, NavModsButton, NavAccountsButton, NavSettingsButton, NavFriendsButton, NavServersButton };
        for (var i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] is not { } button) continue;
            var selected = i == MainTabs.SelectedIndex;
            button.Background = selected ? new SolidColorBrush(Color.FromRgb(28, 41, 35)) : Brushes.Transparent;
            button.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(28, 41, 35)) : Brushes.Transparent;
            button.SetResourceReference(Control.ForegroundProperty, selected ? "Accent" : "Muted");
        }
        if (MainTabs.SelectedIndex == 1) LoadLibraryInstances();
        if (MainTabs.SelectedIndex == 2 && ModsList.ItemsSource is null) SearchMods_Click(sender, e);
        if (MainTabs.SelectedIndex == 3) LoadSavedAccounts();
        if (MainTabs.SelectedIndex == 5) RefreshFriendsUi();
        if (MainTabs.SelectedIndex == 6) RefreshLocalServersUi();
    }

    private void Window_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void RamSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RamLabel is not null) RamLabel.Text = $"{(int)e.NewValue} ГБ";
        if (RamWarningText is null) return;
        RamWarningText.Text = (int)e.NewValue > JvmTuning.RecommendedHeapMb / 1024
            ? "Высокое значение: Windows может замедлиться"
            : "Рекомендуется оставить память системе";
        RamWarningText.Foreground = (int)e.NewValue > JvmTuning.RecommendedHeapMb / 1024
            ? System.Windows.Media.Brushes.Orange
            : (System.Windows.Media.Brush)FindResource("Muted");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void GradientAccent_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _pendingGradientAccent = GradientAccentBox.IsChecked == true;
        UpdateColorWheelLayout();
        UpdatePendingColorPreview();
    }

    private async void BackgroundDim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressSettingsWrites || BackgroundScrim is null) return;
        _backgroundDim = e.NewValue;
        BackgroundScrim.Opacity = _backgroundDim;
        if (BackgroundDimValueLabel != null) BackgroundDimValueLabel.Text = $"{(int)(_backgroundDim * 100)}%";
        await SaveSettingsAsync(_backgroundPath);
    }

    private async void PanelOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressSettingsWrites) return;
        _panelOpacity = e.NewValue;
        if (PanelOpacityValueLabel != null) PanelOpacityValueLabel.Text = $"{(int)(_panelOpacity * 100)}%";
        ApplyAccentTheme();
        await SaveSettingsAsync(_backgroundPath);
    }

    private async void BrowseGameDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку для Minecraft (версии, моды, миры)",
            InitialDirectory = Directory.Exists(_minecraft.InstancesRoot) ? _minecraft.InstancesRoot : "C:\\"
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _gameDirectory = dialog.FolderName;
            _minecraft.SetInstancesRoot(_gameDirectory);
            if (GameDirectoryBox != null) GameDirectoryBox.Text = _gameDirectory;
            if (GameDirectoryStatusText != null) GameDirectoryStatusText.Text = $"Игровая папка изменена на: {_gameDirectory}";
            LoadLibraryInstances();
            await SaveSettingsAsync(_backgroundPath);
            ConfirmDialog.ShowSuccess(this, "Папка игры сохранена", $"Все файлы игры, сборки и моды теперь сохраняются в:\n{_gameDirectory}");
        }
    }

    private void OpenGameDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = _minecraft.InstancesRoot;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConfirmDialog.ShowError(this, "Не удалось открыть папку", ex.Message);
        }
    }

    private void ReextractColors_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_backgroundPath) && File.Exists(_backgroundPath))
        {
            var bmp = LoadBitmapSafely(_backgroundPath);
            if (bmp != null)
            {
                AdaptThemeColorsFromImage(bmp);
                ConfirmDialog.ShowSuccess(this, "Цвета адаптированы", $"Палитра лаунчера обновлена под цвета фона:\nЦвет 1: {_accentColor}\nЦвет 2: {_secondaryAccentColor}");
                return;
            }
        }
        ConfirmDialog.ShowInfo(this, "Автоопределение цветов", "Сначала выберите изображение для фона, чтобы лаунчер определил его главные цвета.");
    }

    private async void ColorWheel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_uiReady || _suppressSettingsWrites) return;
        if (sender is not Image image || image.ActualWidth <= 0 || image.ActualHeight <= 0) return;
        _selectingSecondaryWheel = image.Tag?.ToString() == "Secondary";
        var point = e.GetPosition(image);
        var cx = image.ActualWidth / 2;
        var cy = image.ActualHeight / 2;
        var dx = point.X - cx;
        var dy = point.Y - cy;
        var radius = Math.Min(cx, cy);
        var saturation = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / radius, 0, 1);
        var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        var color = HsvToColor(hue, saturation, AccentBrightnessSlider.Value);
        if (_selectingSecondaryWheel) _pendingSecondaryAccentColor = ToHex(color);
        else _pendingAccentColor = ToHex(color);
        ActiveAccentColorLabel.Text = _selectingSecondaryWheel ? "Цвет 2 · яркость" : "Цвет 1 · яркость";
        UpdatePendingColorPreview();
    }

    private void AccentBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressSettingsWrites) return;
        var color = (Color)ColorConverter.ConvertFromString(_selectingSecondaryWheel ? _pendingSecondaryAccentColor : _pendingAccentColor);
        var (h, s, _) = ColorToHsv(color);
        var adjusted = ToHex(HsvToColor(h, s, e.NewValue));
        if (_selectingSecondaryWheel) _pendingSecondaryAccentColor = adjusted;
        else _pendingAccentColor = adjusted;
        UpdatePendingColorPreview();
    }

    private static WriteableBitmap CreateColorWheelBitmap(int size)
    {
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        var center = (size - 1) / 2.0;
        var radius = center;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - center;
            var dy = y - center;
            var distance = Math.Sqrt(dx * dx + dy * dy) / radius;
            var offset = (y * size + x) * 4;
            if (distance > 1) continue;
            var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            var color = HsvToColor(hue, distance, 1);
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
            pixels[offset + 3] = 255;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        return bitmap;
    }

    private static Color HsvToColor(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static (double Hue, double Saturation, double Value) ColorToHsv(Color color)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var delta = max - min;
        var hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * (((b - r) / delta) + 2) : 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string GetComplementaryAccent(string color)
    {
        var (hue, saturation, value) = ColorToHsv((Color)ColorConverter.ConvertFromString(color));
        return ToHex(HsvToColor((hue + 132) % 360, Math.Max(0.5, saturation * 0.8), value));
    }

    private async void ApplyTheme_Click(object sender, RoutedEventArgs e)
    {
        _accentColor = _pendingAccentColor;
        _secondaryAccentColor = _pendingSecondaryAccentColor;
        _gradientAccent = _pendingGradientAccent;
        ApplyAccentTheme();
        await SaveSettingsAsync(_backgroundPath);
        AccentApplyStatus.Text = "Цвет темы применён";
    }

    private void UpdateColorWheelLayout()
    {
        if (PrimaryColorWheelImage is null) return;
        var wheelSize = _pendingGradientAccent ? 136d : 180d;
        PrimaryColorWheelImage.Width = wheelSize;
        PrimaryColorWheelImage.Height = wheelSize;
        SecondaryColorWheelPanel.Visibility = _pendingGradientAccent ? Visibility.Visible : Visibility.Collapsed;
        SecondaryColorWheelImage.Width = wheelSize;
        SecondaryColorWheelImage.Height = wheelSize;
        AccentModeLabel.Text = _pendingGradientAccent ? "Градиент · выберите два оттенка" : "Матовый цвет · один оттенок";
    }

    private void UpdatePendingColorPreview()
    {
        if (AccentPreview is null) return;
        var primary = (Color)ColorConverter.ConvertFromString(_pendingAccentColor);
        if (_pendingGradientAccent)
        {
            var secondary = (Color)ColorConverter.ConvertFromString(_pendingSecondaryAccentColor);
            AccentPreview.Background = new LinearGradientBrush(primary, secondary, 0);
        }
        else AccentPreview.Background = new SolidColorBrush(primary);
        ActiveAccentColorLabel.Text = _selectingSecondaryWheel ? "Цвет 2 · яркость" : "Цвет 1 · яркость";
    }

    private static bool IsHexColor(string? color) => color is not null && Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$");

    private void ApplyAccentTheme()
    {
        var primary = (Color)ColorConverter.ConvertFromString(_accentColor);
        var secondary = (Color)ColorConverter.ConvertFromString(_secondaryAccentColor);
        var surfaceAccent = _gradientAccent ? TintSurface(primary, secondary, 0.5) : primary;
        if (_gradientAccent)
            Resources["Accent"] = new LinearGradientBrush(primary, secondary, 0);
        else Resources["Accent"] = new SolidColorBrush(primary);

        byte panelAlpha = (byte)Math.Clamp((int)(_panelOpacity * 255), 25, 255);
        byte sidebarAlpha = (byte)Math.Clamp((int)(_panelOpacity * 1.08 * 255), 35, 255);

        var surfaceBase = new SolidColorBrush(TintSurface(Color.FromRgb(12, 16, 18), surfaceAccent, 0.22));
        var panel = new SolidColorBrush(TintSurface(Color.FromRgb(18, 25, 28), surfaceAccent, 0.20, panelAlpha));
        var panel2 = new SolidColorBrush(TintSurface(Color.FromRgb(24, 34, 38), surfaceAccent, 0.24, panelAlpha));
        var sidebar = new SolidColorBrush(TintSurface(Color.FromRgb(15, 21, 24), surfaceAccent, 0.22, sidebarAlpha));
        var hero = new SolidColorBrush(TintSurface(Color.FromRgb(20, 32, 36), surfaceAccent, 0.28, panelAlpha));
        var border = new SolidColorBrush(TintSurface(Color.FromRgb(40, 58, 64), surfaceAccent, 0.35));

        Resources["SurfaceBase"] = surfaceBase;
        Resources["Panel"] = panel;
        Resources["Panel2"] = panel2;
        Resources["SidebarSurface"] = sidebar;
        Resources["HeroSurface"] = hero;
        Resources["SurfaceBorder"] = border;
        Background = surfaceBase;
        BackgroundScrim.Background = new SolidColorBrush(TintSurface(Color.FromRgb(10, 14, 16), surfaceAccent, 0.30));
        Resources["LogoAccent"] = new SolidColorBrush(primary);
        Resources["LogoSecondary"] = new SolidColorBrush(secondary);
        UpdateLauncherIcon(primary, secondary);
        UpdateFxHost();
    }

    private void UpdateFxHost()
    {
        if (FxHost == null) return;
        var col1 = (Color)ColorConverter.ConvertFromString(_accentColor);
        var col2 = (Color)ColorConverter.ConvertFromString(_secondaryAccentColor);
        FxHost.Configure(_fxType, _fxDensity, _fxSpeed, col1, col2, _gradientAccent);
    }

    private async void FxTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressSettingsWrites) return;
        _fxType = (FxTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";
        UpdateFxHost();
        await SaveSettingsAsync(_backgroundPath);
    }

    private async void FxDensitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressSettingsWrites) return;
        _fxDensity = (int)e.NewValue;
        if (FxDensityLabel != null) FxDensityLabel.Text = $"{_fxDensity} шт.";
        UpdateFxHost();
        await SaveSettingsAsync(_backgroundPath);
    }

    private async void FxSpeedSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressSettingsWrites) return;
        _fxSpeed = Math.Round(e.NewValue, 1);
        if (FxSpeedLabel != null) FxSpeedLabel.Text = $"{_fxSpeed:F1}x";
        UpdateFxHost();
        await SaveSettingsAsync(_backgroundPath);
    }

    private static Color TintSurface(Color baseColor, Color accent, double amount, byte alpha = 255) => Color.FromArgb(
        alpha,
        (byte)Math.Round(baseColor.R * (1 - amount) + accent.R * amount),
        (byte)Math.Round(baseColor.G * (1 - amount) + accent.G * amount),
        (byte)Math.Round(baseColor.B * (1 - amount) + accent.B * amount));

    private void UpdateLauncherIcon(Color primary, Color secondary)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            Brush fill = _gradientAccent ? new LinearGradientBrush(primary, secondary, 0) : new SolidColorBrush(primary);
            context.DrawRoundedRectangle(fill, null, new Rect(2, 2, 60, 60), 14, 14);
            var dark = new SolidColorBrush(Color.FromRgb(14, 18, 16));
            var large = new FormattedText("KS", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 29, dark, 1.0);
            context.DrawText(large, new Point((64 - large.Width) / 2, 5));
            var small = new FormattedText("LAUNCHER", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 7, dark, 1.0);
            context.DrawText(small, new Point((64 - small.Width) / 2, 43));
        }
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        Icon = bitmap;
    }

    private void InitializeSocialAndServers()
    {
        _friends.FriendsChanged += () => Dispatcher.Invoke(RefreshFriendsUi);
        _friends.InviteReceived += invite => Dispatcher.Invoke(() =>
        {
            RefreshFriendsUi();
            StatusText.Text = $"Входящее приглашение от {invite.FromUsername}!";
        });

        _localServers.ConsoleLineReceived += line => Dispatcher.Invoke(() =>
        {
            if (ServerConsoleBox != null)
            {
                ServerConsoleBox.AppendText(line + Environment.NewLine);
                ServerConsoleBox.ScrollToEnd();
            }
        });
        _localServers.ServerStateChanged += () => Dispatcher.Invoke(UpdateSelectedServerStateUi);

        Closed += (_, _) =>
        {
            _friends.Dispose();
            _localServers.Dispose();
        };

        _friends.StartLanSync(GetMyCurrentPresence);
        LocalLanIpText.Text = $"Ваш LAN IP: {FriendsService.GetLocalLanIp()}";
        RefreshFriendsUi();
        RefreshLocalServersUi();
    }

    private (string Username, string AccountSystem, FriendPresence Presence) GetMyCurrentPresence()
    {
        var active = _accountManager.GetActiveAccount();
        var username = active?.Username ?? OfflineNameBox?.Text.Trim() ?? "Player";
        var system = active?.Type?.ToLowerInvariant() switch
        {
            "microsoft" => "Microsoft",
            "ely" => "Ely.by",
            "ks" => "KS System",
            _ => "Offline"
        };

        var runningServer = _localServers.RunningServer;
        var presence = new FriendPresence
        {
            IsOnline = true,
            IsPlaying = _runningGameProcess != null || runningServer != null,
            ServerName = runningServer?.Name,
            ServerIp = runningServer != null ? FriendsService.GetLocalLanIp() : null,
            ServerPort = runningServer?.Port ?? 25565,
            GameVersion = runningServer?.Version ?? (VersionBox?.SelectedItem as MinecraftVersion)?.Id ?? "1.20.4",
            Loader = runningServer?.Core ?? (GameLoaderBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "vanilla",
            IsLocalHostedServer = runningServer != null
        };
        return (username, system, presence);
    }

    private void RefreshFriendsUi()
    {
        if (MyFriendCodeText == null || FriendsList == null) return;
        MyFriendCodeText.Text = _friends.MyFriendCode;
        var active = _accountManager.GetActiveAccount();
        var sysLabel = active?.Type?.ToLowerInvariant() switch
        {
            "microsoft" => "Microsoft",
            "ely" => "Ely.by",
            "ks" => "KS System",
            _ => "Offline"
        };
        MyFriendSystemText.Text = $"Система: {sysLabel} ({active?.Username ?? OfflineNameBox.Text.Trim()})";

        FriendsList.ItemsSource = null;
        FriendsList.ItemsSource = _friends.Friends;
        FriendsCountText.Text = $"{_friends.Friends.Count} друз.";

        InvitesList.ItemsSource = null;
        InvitesList.ItemsSource = _friends.PendingInvites;
        InvitesBanner.Visibility = _friends.PendingInvites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyMyFriendCode_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_friends.MyFriendCode);
        StatusText.Text = $"KS Friend ID скопирован: {_friends.MyFriendCode}";
    }

    private void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sys = (AddFriendSystemBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Авто";
            if (sys.StartsWith("Авто")) sys = "Авто";
            var friend = _friends.AddFriend(AddFriendInputBox.Text, sys);
            AddFriendInputBox.Clear();
            StatusText.Text = $"Друг {friend.Username} ({friend.AccountSystem}) добавлен!";
        }
        catch (Exception ex)
        {
            ConfirmDialog.ShowError(this, "Не удалось добавить друга", ex.Message);
        }
    }

    private void SeedTestFriends_Click(object sender, RoutedEventArgs e)
    {
        _friends.SeedTestFriends();
        StatusText.Text = "Тестовые друзья добавлены для проверки всех сценариев.";
    }

    private void SimulateModpackInvite_Click(object sender, RoutedEventArgs e)
    {
        var invite = _friends.SimulateModpackInvite();
        StatusText.Text = $"Получено тестовое приглашение от {invite.FromUsername} в сборку «{invite.Modpack?.Title}»!";
    }

    private void RemoveFriend_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FriendProfile friend) return;
        _friends.RemoveFriend(friend.FriendId);
    }

    private void InviteFriend_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FriendProfile friend) return;
        var my = GetMyCurrentPresence();
        if (string.IsNullOrWhiteSpace(my.Presence.ServerIp))
        {
            ConfirmDialog.ShowInfo(this, "Нет активного сервера", "Запустите локальный сервер во вкладке «Серверы», чтобы позвать друга на него.");
            return;
        }
        _friends.SendInvite(friend, my.Username, my.AccountSystem, my.Presence);
        StatusText.Text = $"Приглашение на {my.Presence.ServerIp}:{my.Presence.ServerPort} отправлено {friend.Username}!";
    }

    private async void JoinFriend_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FriendProfile friend) return;
        var p = friend.Presence;
        if (!p.CanJoinServer)
        {
            ConfirmDialog.ShowInfo(this, "Друг не на сервере", $"{friend.Username} сейчас не играет на сервере.");
            return;
        }

        var version = string.IsNullOrWhiteSpace(p.GameVersion) ? "1.20.4" : p.GameVersion!;
        var loader = string.IsNullOrWhiteSpace(p.Loader) ? "vanilla" : p.Loader!;

        if (p.HasModpack)
        {
            var ok = ConfirmDialog.Show(this, "Сборка друга", $"{friend.Username} играет на сборке «{p.Modpack!.Title}» ({version} • {loader}).\nСкачать сборку и сразу зайти на сервер {p.ServerIp}:{p.ServerPort}?", "Скачать и зайти", "Отмена");
            if (!ok) return;
            await DownloadModpackAndJoinAsync(p.Modpack, p.ServerIp!, p.ServerPort);
            return;
        }

        if (p.HasMods)
        {
            var dlg = new JoinFriendDialog(friend, _minecraft.Instances.GetInstances()) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            switch (dlg.SelectedMode)
            {
                case JoinFriendMode.WithFriendMods:
                    await InstallFriendModsAndJoinAsync(friend, p.ServerIp!, p.ServerPort);
                    break;
                case JoinFriendMode.WithOwnMods when dlg.SelectedOwnInstance is { } own:
                    NavigateTo(0);
                    await LaunchGameCoreAsync(own.Version, own.Loader, own.FolderPath, p.ServerIp, p.ServerPort);
                    break;
                case JoinFriendMode.WithoutMods:
                    NavigateTo(0);
                    await LaunchGameCoreAsync(version, "vanilla", null, p.ServerIp, p.ServerPort);
                    break;
            }
            return;
        }

        NavigateTo(0);
        await LaunchGameCoreAsync(version, loader, null, p.ServerIp, p.ServerPort);
    }

    private async void AcceptInvite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FriendInvite invite) return;
        _friends.DismissInvite(invite.Id);

        if (invite.Modpack != null)
        {
            await DownloadModpackAndJoinAsync(invite.Modpack, invite.ServerIp, invite.ServerPort);
            return;
        }

        NavigateTo(0);
        await LaunchGameCoreAsync(invite.GameVersion, invite.Loader, null, invite.ServerIp, invite.ServerPort);
    }

    private void DismissInvite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FriendInvite invite) return;
        _friends.DismissInvite(invite.Id);
    }

    private async Task InstallFriendModsAndJoinAsync(FriendProfile friend, string serverIp, int serverPort)
    {
        var p = friend.Presence;
        var version = p.GameVersion ?? "1.20.4";
        var loader = p.Loader ?? "fabric";
        var instanceName = $"Friend-{friend.Username}";

        NavigateTo(0);
        PlayButton.IsEnabled = false;
        InstallProgress.IsIndeterminate = true;
        ProgressText.Text = "МОДЫ ДРУГА";
        StatusText.Text = $"Подготовка сборки с модами {friend.Username} ({p.Mods.Count} мод.)...";

        try
        {
            var existing = _minecraft.Instances.GetInstances()
                .FirstOrDefault(i => i.Name.Equals(instanceName, StringComparison.OrdinalIgnoreCase) && i.Version == version && i.Loader == loader);
            var instance = existing ?? _minecraft.Instances.CreateInstance(instanceName, version, loader);

            for (int i = 0; i < p.Mods.Count; i++)
            {
                var mod = p.Mods[i];
                StatusText.Text = $"Скачивание мода друга ({i + 1}/{p.Mods.Count}): {mod.Title}...";
                var proj = new ModrinthProject(mod.ProjectId ?? mod.Slug, mod.Slug, mod.Title, "", 0, "mod");
                await _modrinth.InstallAsync(proj, "mod", loader, version, instance.FolderPath);
            }

            InstallProgress.IsIndeterminate = false;
            await LaunchGameCoreAsync(version, loader, instance.FolderPath, serverIp, serverPort);
        }
        catch (Exception ex)
        {
            InstallProgress.IsIndeterminate = false;
            ProgressText.Text = "ОШИБКА";
            PlayButton.IsEnabled = true;
            ConfirmDialog.ShowError(this, "Ошибка установки модов друга", ex.Message);
        }
    }

    private async Task DownloadModpackAndJoinAsync(FriendModpackInfo pack, string serverIp, int serverPort)
    {
        NavigateTo(0);
        PlayButton.IsEnabled = false;
        InstallProgress.IsIndeterminate = true;
        ProgressText.Text = "СБОРКА";
        StatusText.Text = $"Скачивание сборки «{pack.Title}» ({pack.Version} • {pack.Loader})...";

        try
        {
            var existing = _minecraft.Instances.GetInstances()
                .FirstOrDefault(i => i.Name.Equals(pack.Title, StringComparison.OrdinalIgnoreCase) && i.Version == pack.Version);
            var instance = existing ?? _minecraft.Instances.CreateInstance(pack.Title, pack.Version, pack.Loader);

            if (existing == null)
            {
                var proj = new ModrinthProject(pack.ProjectId, pack.Slug, pack.Title, "", 0, "modpack");
                await _modrinth.InstallAsync(proj, "modpack", pack.Loader, pack.Version, instance.FolderPath);
            }

            InstallProgress.IsIndeterminate = false;
            await LaunchGameCoreAsync(pack.Version, pack.Loader, instance.FolderPath, serverIp, serverPort);
        }
        catch (Exception ex)
        {
            InstallProgress.IsIndeterminate = false;
            ProgressText.Text = "ОШИБКА";
            PlayButton.IsEnabled = true;
            ConfirmDialog.ShowError(this, "Ошибка загрузки сборки", ex.Message);
        }
    }

    private void RefreshLocalServersUi()
    {
        if (LocalServersList == null) return;
        var servers = _localServers.GetServers();
        LocalServersList.ItemsSource = servers;
        if (_selectedLocalServer != null)
        {
            _selectedLocalServer = servers.FirstOrDefault(s => s.Id == _selectedLocalServer.Id);
        }
        if (_selectedLocalServer == null && servers.Count > 0)
        {
            LocalServersList.SelectedIndex = 0;
        }
        UpdateSelectedServerStateUi();
    }

    private void LocalServersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedLocalServer = LocalServersList.SelectedItem as LocalServerConfig;
        UpdateSelectedServerStateUi();
    }

    private void UpdateSelectedServerStateUi()
    {
        if (SelectedServerTitle == null) return;
        if (_selectedLocalServer == null)
        {
            SelectedServerTitle.Text = "Выберите или создайте сервер";
            SelectedServerStatus.Text = "Статус: не выбран";
            StartServerBtn.IsEnabled = false;
            StopServerBtn.IsEnabled = false;
            return;
        }

        var isThisRunning = _localServers.IsRunning && _localServers.RunningServer?.Id == _selectedLocalServer.Id;
        SelectedServerTitle.Text = $"{_selectedLocalServer.Name} ({_selectedLocalServer.Core.ToUpperInvariant()} {_selectedLocalServer.Version})";
        SelectedServerStatus.Text = isThisRunning
            ? $"Статус: ЗАПУЩЕН на порту {_selectedLocalServer.Port} • {_selectedLocalServer.ModeBadge} • {_selectedLocalServer.RamMb / 1024} ГБ ОЗУ"
            : $"Статус: остановлен • {_selectedLocalServer.Summary}";

        StartServerBtn.IsEnabled = !_localServers.IsRunning;
        StopServerBtn.IsEnabled = isThisRunning;
        EditServerPirateBox.IsChecked = !_selectedLocalServer.OnlineMode;

        foreach (ComboBoxItem item in EditServerRamBox.Items)
        {
            if (item.Tag?.ToString() == _selectedLocalServer.RamMb.ToString())
            {
                EditServerRamBox.SelectedItem = item;
                break;
            }
        }
    }

    private void CreateLocalServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var version = (ServerVersionBox.SelectedItem as MinecraftVersion)?.Id ?? "1.20.4";
            var core = (ServerCoreBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "vanilla";
            var ramMb = int.TryParse((ServerRamBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var r) ? r : 2048;
            var port = int.TryParse(ServerPortBox.Text.Trim(), out var p) ? p : 25565;
            var maxPlayers = int.TryParse(ServerMaxPlayersBox.Text.Trim(), out var mp) ? mp : 20;
            var gamemode = (ServerGamemodeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "survival";
            var difficulty = (ServerDifficultyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "normal";
            var onlineMode = ServerPirateBox.IsChecked != true;

            var created = _localServers.CreateServer(
                name: ServerNameBox.Text,
                version: version,
                core: core,
                ramMb: ramMb,
                port: port,
                onlineMode: onlineMode,
                motd: ServerMotdBox.Text,
                maxPlayers: maxPlayers,
                difficulty: difficulty,
                gamemode: gamemode,
                pvp: ServerPvpBox.IsChecked == true,
                whiteList: ServerWhitelistBox.IsChecked == true
            );

            _selectedLocalServer = created;
            RefreshLocalServersUi();
            StatusText.Text = $"Локальный сервер «{created.Name}» создан ({created.Summary})!";
        }
        catch (Exception ex)
        {
            ConfirmDialog.ShowError(this, "Ошибка создания сервера", ex.Message);
        }
    }

    private async void StartLocalServer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocalServer == null) return;
        try
        {
            ServerConsoleBox.Clear();
            StartServerBtn.IsEnabled = false;
            await _localServers.StartServerAsync(_selectedLocalServer, _selectedJavaPath, status => Dispatcher.Invoke(() => StatusText.Text = status));
        }
        catch (Exception ex)
        {
            ConfirmDialog.ShowError(this, "Не удалось запустить сервер", ex.Message);
            UpdateSelectedServerStateUi();
        }
    }

    private async void StopLocalServer_Click(object sender, RoutedEventArgs e)
    {
        StopServerBtn.IsEnabled = false;
        await _localServers.StopServerAsync();
        UpdateSelectedServerStateUi();
    }

    private void SaveSelectedServerSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocalServer == null) return;
        _selectedLocalServer.OnlineMode = EditServerPirateBox.IsChecked != true;
        if (int.TryParse((EditServerRamBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var ram))
            _selectedLocalServer.RamMb = ram;
        _localServers.SaveServerConfig(_selectedLocalServer);
        RefreshLocalServersUi();
        StatusText.Text = $"Настройки сервера «{_selectedLocalServer.Name}» сохранены в server.properties.";
    }

    private void CopyLocalServerIp_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocalServer == null) return;
        var ip = $"{FriendsService.GetLocalLanIp()}:{_selectedLocalServer.Port}";
        Clipboard.SetText(ip);
        StatusText.Text = $"Адрес сервера скопирован: {ip}";
    }

    private async void JoinOwnServer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocalServer == null) return;
        var loader = _selectedLocalServer.Core == "fabric" ? "fabric" : "vanilla";
        NavigateTo(0);
        await LaunchGameCoreAsync(_selectedLocalServer.Version, loader, null, "127.0.0.1", _selectedLocalServer.Port);
    }

    private void InviteFriendsToServer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocalServer == null) return;
        var my = GetMyCurrentPresence();
        var presence = new FriendPresence
        {
            IsOnline = true,
            IsPlaying = true,
            ServerName = _selectedLocalServer.Name,
            ServerIp = FriendsService.GetLocalLanIp(),
            ServerPort = _selectedLocalServer.Port,
            GameVersion = _selectedLocalServer.Version,
            Loader = _selectedLocalServer.Core == "fabric" ? "fabric" : "vanilla",
            IsLocalHostedServer = true
        };
        _friends.SendInvite(null, my.Username, my.AccountSystem, presence);
        StatusText.Text = $"Приглашение на сервер «{_selectedLocalServer.Name}» ({presence.ServerIp}:{presence.ServerPort}) отправлено друзьям!";
    }

    private void DeleteLocalServer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocalServer == null) return;
        if (!ConfirmDialog.Show(this, "Удалить сервер?", $"Удалить локальный сервер «{_selectedLocalServer.Name}» и все его файлы?", "Удалить", "Отмена", isDestructive: true))
            return;
        try
        {
            _localServers.DeleteServer(_selectedLocalServer);
            _selectedLocalServer = null;
            RefreshLocalServersUi();
        }
        catch (Exception ex)
        {
            ConfirmDialog.ShowError(this, "Ошибка удаления", ex.Message);
        }
    }

    private void SendServerCommand_Click(object sender, RoutedEventArgs e)
    {
        var cmd = ServerCommandBox.Text.Trim();
        if (string.IsNullOrEmpty(cmd)) return;
        _localServers.SendCommand(cmd);
        ServerCommandBox.Clear();
    }

    private void ServerCommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendServerCommand_Click(sender, e);
    }

    private sealed record LauncherSettings(
        string? BackgroundPath, 
        bool IncludeSnapshots, 
        string? AccentColor = null, 
        bool GradientAccent = false, 
        double BackgroundDim = 0.70, 
        string? SecondaryAccentColor = null,
        string? GameLanguage = "ru",
        string? JavaPath = null,
        string? ElyWorkerUrl = null,
        string? GameDirectory = null,
        double PanelOpacity = 0.85,
        string FxType = "none",
        int FxDensity = 60,
        double FxSpeed = 1.0
    );
}
