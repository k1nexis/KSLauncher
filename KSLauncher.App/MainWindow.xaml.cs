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
    object RawProject
);

public partial class MainWindow : Window
{
    private readonly MinecraftService _minecraft = new();
    private readonly ModrinthService _modrinth = new();
    private readonly CurseForgeService _curseforge = new();
    private readonly MinecraftVersionService _versions = new();
    private readonly OfflineProfileStore _profiles = new();
    private readonly MicrosoftAccountService _microsoft = new();
    private readonly ElyAccountService _ely = new();
    private readonly AuthlibInjectorService _authlibInjector = new();
    private readonly CloudAccountService _cloud = new();
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "launcher-settings.json");

    private MSession? _activeMicrosoftSession;
    private MSession? _activeElySession;
    private IReadOnlyList<MinecraftVersion> _allVersions = [];
    private bool _uiReady;
    private bool _suppressVersionReload;
    private string? _backgroundPath;
    private string _accentColor = "#A6F36B";
    private string _secondaryAccentColor = "#69B7FF";
    private string _pendingAccentColor = "#A6F36B";
    private string _pendingSecondaryAccentColor = "#69B7FF";
    private double _backgroundDim = 0.70;
    private bool _gradientAccent;
    private bool _pendingGradientAccent;
    private bool _selectingSecondaryWheel;
    private bool _suppressSettingsWrites;
    private string _gameLanguage = "ru";
    private string? _activeCustomInstancePath;

    public MainWindow()
    {
        InitializeComponent();
        _uiReady = true;
        HardwareLabel.Text = JvmTuning.HardwareSummary;
        RamSlider.Value = JvmTuning.RecommendedHeapMb / 1024.0;
        PrimaryColorWheelImage.Source = CreateColorWheelBitmap(180);
        SecondaryColorWheelImage.Source = CreateColorWheelBitmap(180);
        ApplyAccentTheme();
        UpdatePendingColorPreview();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await RestoreProfileAsync();
        await RestoreSettingsAsync();
        await LoadVersionsAsync();
        LoadLibraryInstances();
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

            _accentColor = IsHexColor(settings?.AccentColor) ? settings!.AccentColor! : "#A6F36B";
            _secondaryAccentColor = IsHexColor(settings?.SecondaryAccentColor) ? settings!.SecondaryAccentColor! : GetComplementaryAccent(_accentColor);
            _pendingAccentColor = _accentColor;
            _pendingSecondaryAccentColor = _secondaryAccentColor;
            _gradientAccent = settings?.GradientAccent == true;
            _pendingGradientAccent = _gradientAccent;
            GradientAccentBox.IsChecked = _pendingGradientAccent;
            UpdateColorWheelLayout();
            ApplyAccentTheme();
            UpdatePendingColorPreview();

            _backgroundDim = settings?.BackgroundDim is >= 0.25 and <= 0.9 ? settings.BackgroundDim : 0.70;
            BackgroundDimSlider.Value = _backgroundDim;
            BackgroundScrim.Opacity = _backgroundDim;

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

            if (!string.IsNullOrWhiteSpace(settings?.BackgroundPath) && File.Exists(settings.BackgroundPath))
            {
                ApplyBackground(settings.BackgroundPath);
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
            _gameLanguage
        ), new JsonSerializerOptions { WriteIndented = true }));
    }

    // ==========================================
    // ЗАПУСК ИГРЫ
    // ==========================================

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (VersionBox.SelectedItem is not MinecraftVersion selectedVersion) return;
        var version = selectedVersion.Id;
        var loader = (GameLoaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "vanilla";
        var username = OfflineNameBox.Text.Trim();
        var ramMb = (int)RamSlider.Value * 1024;

        if (ramMb > JvmTuning.RecommendedHeapMb + 1024)
        {
            var answer = MessageBox.Show(this, "Выделено почти столько же памяти, сколько рекомендует система. Это может замедлить Windows. Продолжить?", "Проверка памяти", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        PlayButton.IsEnabled = false;
        QuickRepairButton.Visibility = Visibility.Collapsed;
        InstallProgress.Value = 0;
        ProgressText.Text = "УСТАНОВКА";
        StatusText.Text = $"Проверяю файлы Minecraft {version} ({loader}). При первом запуске потребуется скачать игровые файлы.";

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

            InstallProgress.IsIndeterminate = true;
            await _minecraft.LaunchAsync(
                version: version,
                loader: loader,
                session: session,
                ramMb: ramMb,
                stringDeduplication: StringDedupBox.IsChecked == true,
                authlibInjectorPath: authlibInjectorPath,
                customInstanceRoot: _activeCustomInstancePath,
                gameLanguage: _gameLanguage
            );

            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = 100;
            ProgressText.Text = "ЗАПУЩЕНО";
            StatusText.Text = $"Minecraft {version} ({loader}) успешно запущен для {session.Username}!";
            LoadLibraryInstances();
        }
        catch (Exception ex)
        {
            InstallProgress.IsIndeterminate = false;
            ProgressText.Text = "ОШИБКА";
            StatusText.Text = $"Ошибка запуска: {ex.Message}";
            QuickRepairButton.Visibility = Visibility.Visible;
            MessageBox.Show(this, $"{ex.Message}\n\nВы можете воспользоваться кнопкой «Починить сборку» для восстановления файлов.", "Не удалось запустить Minecraft", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PlayButton.IsEnabled = true;
        }
    }

    private void GameLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressSettingsWrites) return;
        _gameLanguage = (GameLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ru";
        _ = SaveSettingsAsync(_backgroundPath);
    }

    // ==========================================
    // БИБЛИОТЕКА И УПРАВЛЕНИЕ СБОРКАМИ (Requirement 1 & 10)
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

    private void CreateInstance_Click(object sender, RoutedEventArgs e)
    {
        var name = NewInstanceNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Укажите название новой версии.", "Создание сборки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (NewInstanceVersionBox.SelectedItem is not MinecraftVersion version)
        {
            MessageBox.Show(this, "Выберите версию Minecraft.", "Создание сборки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var loader = (NewInstanceLoaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "fabric";

        try
        {
            var instance = _minecraft.Instances.CreateInstance(name, version.Id, loader);
            LoadLibraryInstances();
            StatusText.Text = $"Сборка «{instance.Name}» ({version.Id} · {loader}) успешно создана!";
            MessageBox.Show(this, $"Сборка «{instance.Name}» создана!\nПапка: {instance.FolderPath}\n\nТеперь вы можете выбрать её и запустить из библиотеки.", "Сборка создана", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось создать сборку: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LaunchLibraryInstance_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameInstanceInfo instance) return;
        _activeCustomInstancePath = instance.FolderPath;

        // Sync version and loader dropdowns on Home screen
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
        await RunRepairWorkflowAsync(instance.FolderPath, instance.Version, instance.Loader, instance.Name);
    }

    private async void QuickRepair_Click(object sender, RoutedEventArgs e)
    {
        var version = (VersionBox.SelectedItem as MinecraftVersion)?.Id ?? "1.20.4";
        var loader = (GameLoaderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "vanilla";
        var path = _activeCustomInstancePath ?? _minecraft.GetInstanceRoot(version, loader);
        await RunRepairWorkflowAsync(path, version, loader, "Текущая сборка");
    }

    private async Task RunRepairWorkflowAsync(string instanceRoot, string version, string loader, string instanceName)
    {
        var confirm = MessageBox.Show(this,
            $"Запустить восстановление «{instanceName}»?\n\nЛаунчер:\n1. Очистит повреждённые временные файлы и битый кэш.\n2. Проверит и исключит повреждённые моды в папке mods.\n3. Заново переустановит ядро {loader} и официальные библиотеки игры.\n\nВаши сохранения миров, скриншоты и настройки останутся нетронутыми.",
            "Починить сборку", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

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
            StatusText.Text = $"Сборка «{instanceName}» полностью починена и готова к запуску!";
            QuickRepairButton.Visibility = Visibility.Collapsed;
            LoadLibraryInstances();
            MessageBox.Show(this, report, "Восстановление завершено", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            InstallProgress.IsIndeterminate = false;
            ProgressText.Text = "ОШИБКА";
            StatusText.Text = $"Ошибка починки: {ex.Message}";
            MessageBox.Show(this, $"Не удалось завершить восстановление: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PlayButton.IsEnabled = true;
        }
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
        var answer = MessageBox.Show(this, $"Удалить сборку «{instance.Name}» ({instance.FolderPath})?\nВсе моды и файлы этой сборки будут удалены.", "Удаление сборки", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                _minecraft.Instances.DeleteInstance(instance.FolderPath);
                LoadLibraryInstances();
                StatusText.Text = $"Сборка «{instance.Name}» удалена.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Ошибка удаления: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var gameVersion = (ModVersionBox.SelectedItem as MinecraftVersion)?.Id ?? "";
        var query = ModSearchBox.Text.Trim();

        SearchModsButton.IsEnabled = false;
        try
        {
            StatusText.Text = $"Поиск в {(platform == "curseforge" ? "CurseForge" : "Modrinth")}…";
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
                        PlatformColor: "#FF8C42",
                        RawProject: cf
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
                        PlatformColor: "#A6F36B",
                        RawProject: mr
                    ));
                }
            }

            ModsList.ItemsSource = unifiedList;
            StatusText.Text = $"Найдено проектов в {platform}: {unifiedList.Count}. Нажмите «Скачать в лаунчере» для установки.";
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
            StatusText.Text = $"Загружаю «{item.Title}» из {item.Platform}…";

            string fileName;
            string targetDir;
            IReadOnlyList<string> dependencies = [];

            if (item.Platform == "CurseForge" && item.RawProject is CurseForgeProject cfProject)
            {
                fileName = await _curseforge.InstallAsync(cfProject, type, loader, version, targetInstanceRoot);
                targetDir = Path.Combine(targetInstanceRoot, $"{version}-{loader.ToLowerInvariant()}", type == "mod" ? "mods" : type);
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
                ? $"Установлено: {fileName}. Зависимостей: {dependencies.Count}. Сборка: {loader}."
                : $"Установлено в {targetDir}: {fileName}.";

            LoadLibraryInstances();

            MessageBox.Show(this, $"Файл: {fileName}\nПапка: {targetDir}" + (dependencies.Count > 0 ? $"\n\nОбязательные зависимости:\n{string.Join("\n", dependencies)}" : ""), "Загрузка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Загрузка не выполнена: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Ошибка загрузки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==========================================
    // ФОН И АДАПТИВНЫЕ ЦВЕТА (Requirement 4)
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
            // Видео воспроизводится параллельно фоновому изображению
            VideoBackground.Source = new Uri(path);
            VideoBackground.Visibility = Visibility.Visible;
            VideoBackground.Play();

            // Проверяем, есть ли рядом картинка-постер для адаптации цветов
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

    /// <summary>
    /// Автоматически извлекает выразительный доминирующий цвет фона и адаптирует тему оформления.
    /// </summary>
    private void AdaptThemeColorsFromImage(BitmapSource bitmap)
    {
        try
        {
            var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int width = formatted.PixelWidth;
            int height = formatted.PixelHeight;
            if (width <= 0 || height <= 0) return;

            int stepX = Math.Max(1, width / 20);
            int stepY = Math.Max(1, height / 20);
            var stride = width * 4;
            var pixels = new byte[stride * height];
            formatted.CopyPixels(pixels, stride, 0);

            double bestScore = -1;
            Color bestColor = (Color)ColorConverter.ConvertFromString("#A6F36B");

            for (int y = 0; y < height; y += stepY)
            {
                for (int x = 0; x < width; x += stepX)
                {
                    int index = (y * stride) + (x * 4);
                    byte b = pixels[index];
                    byte g = pixels[index + 1];
                    byte r = pixels[index + 2];
                    var color = Color.FromRgb(r, g, b);
                    var (h, s, v) = ColorToHsv(color);

                    // Ищем цвет с умеренной насыщенностью и хорошей яркостью
                    if (s > 0.30 && v > 0.35 && v < 0.95)
                    {
                        double score = (s * 1.6) + (1.0 - Math.Abs(v - 0.75));
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestColor = HsvToColor(h, Math.Min(1.0, s * 1.15), Math.Clamp(v * 1.15, 0.70, 0.96));
                        }
                    }
                }
            }

            if (bestScore > 0)
            {
                _accentColor = ToHex(bestColor);
                _secondaryAccentColor = GetComplementaryAccent(_accentColor);
                _pendingAccentColor = _accentColor;
                _pendingSecondaryAccentColor = _secondaryAccentColor;
                ApplyAccentTheme();
                UpdatePendingColorPreview();
            }
        }
        catch { }
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
    // АККАУНТЫ И АВТОРИЗАЦИЯ
    // ==========================================

    private async void SaveOffline_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = await _profiles.SaveAsync(OfflineNameBox.Text.Trim());
            SidebarProfile.Text = $"{profile.Username} • Offline";
            StatusText.Text = $"Сохранён локальный профиль {profile.Username} (UUID {profile.Uuid}).";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Некорректный ник", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void MicrosoftLogin_Click(object sender, RoutedEventArgs e)
    {
        MicrosoftLoginButton.IsEnabled = false;
        MicrosoftLoginButton.Content = "Ожидание входа…";
        try
        {
            _activeMicrosoftSession = await _microsoft.SignInAsync();
            _activeElySession = null;
            SidebarProfile.Text = $"{_activeMicrosoftSession.Username} • Microsoft";
            StatusText.Text = $"Официальный вход выполнен: {_activeMicrosoftSession.Username}.";
            MicrosoftLoginButton.Content = "Подключено: " + _activeMicrosoftSession.Username;
        }
        catch (Exception ex)
        {
            MicrosoftLoginButton.Content = "Войти через Microsoft";
            StatusText.Text = "Вход Microsoft не завершён.";
            MessageBox.Show(this, ex.Message, "Вход Microsoft", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { MicrosoftLoginButton.IsEnabled = true; }
    }

    private async void ElyLogin_Click(object sender, RoutedEventArgs e)
    {
        ElyLoginButton.IsEnabled = false;
        ElyStatusText.Text = "Открываю безопасный вход Ely.by…";
        try
        {
            var account = await _ely.SignInAsync(CloudUrlBox.Text.Trim());
            _activeElySession = new MSession(account.Username, account.Uuid, account.AccessToken) { UserType = "ely" };
            _activeMicrosoftSession = null;
            SidebarProfile.Text = $"{account.Username} • Ely.by";
            ElyStatusText.Text = $"Подключено: {account.Username}. Скин Ely.by будет загружаться через authlib-injector.";
            ElyLoginButton.Content = "Подключено: " + account.Username;
            StatusText.Text = $"Вход Ely.by выполнен: {account.Username}.";
        }
        catch (Exception ex)
        {
            ElyStatusText.Text = "Вход Ely.by не завершён.";
            MessageBox.Show(this, ex.Message, "Вход Ely.by", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { ElyLoginButton.IsEnabled = true; }
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
            CloudStatusText.Text = $"KS System подключена: {username}. Токен в памяти до закрытия лаунчера.";
            CloudLogoutButton.IsEnabled = true;
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
    // НАВИГАЦИЯ И ВНЕШНИЙ ВИД
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
        var buttons = new[] { NavHomeButton, NavLibraryButton, NavModsButton, NavAccountsButton, NavSettingsButton };
        for (var i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] is not { } button) continue;
            var selected = i == MainTabs.SelectedIndex;
            button.Background = selected ? new SolidColorBrush(Color.FromRgb(28, 41, 35)) : Brushes.Transparent;
            button.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(28, 41, 35)) : Brushes.Transparent;
            button.SetResourceReference(Control.ForegroundProperty, selected ? "Accent" : "Muted");
        }
        if (MainTabs.SelectedIndex == 1) LoadLibraryInstances();
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
        await SaveSettingsAsync(_backgroundPath);
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
        Resources["SurfaceBase"] = new SolidColorBrush(TintSurface(Color.FromRgb(11, 15, 14), surfaceAccent, 0.12));
        Resources["Panel"] = new SolidColorBrush(TintSurface(Color.FromRgb(18, 25, 23), surfaceAccent, 0.10, 0xD9));
        Resources["Panel2"] = new SolidColorBrush(TintSurface(Color.FromRgb(24, 34, 31), surfaceAccent, 0.13, 0xD9));
        Resources["SidebarSurface"] = new SolidColorBrush(TintSurface(Color.FromRgb(16, 22, 20), surfaceAccent, 0.10, 0xEE));
        Resources["HeroSurface"] = new SolidColorBrush(TintSurface(Color.FromRgb(20, 34, 28), surfaceAccent, 0.18));
        Resources["SurfaceBorder"] = new SolidColorBrush(TintSurface(Color.FromRgb(38, 55, 45), surfaceAccent, 0.22));
        BackgroundScrim.Background = new SolidColorBrush(TintSurface(Color.FromRgb(9, 13, 12), surfaceAccent, 0.22));
        Resources["LogoAccent"] = new SolidColorBrush(primary);
        Resources["LogoSecondary"] = new SolidColorBrush(secondary);
        UpdateLauncherIcon(primary, secondary);
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

    private sealed record LauncherSettings(
        string? BackgroundPath, 
        bool IncludeSnapshots, 
        string? AccentColor = null, 
        bool GradientAccent = false, 
        double BackgroundDim = 0.70, 
        string? SecondaryAccentColor = null,
        string? GameLanguage = "ru"
    );
}
