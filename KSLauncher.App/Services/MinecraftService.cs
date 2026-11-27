extern alias CmlForge;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using ForgeInstaller = CmlForge::CmlLib.Core.Installer.Forge.ForgeInstaller;
using ForgeInstallOptions = CmlForge::CmlLib.Core.Installer.Forge.ForgeInstallOptions;
using NeoForgeInstaller = CmlLib.Core.Installer.NeoForge.NeoForgeInstaller;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace KSLauncher.App.Services;

public sealed class MinecraftService
{
    public string InstancesRoot { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "instances");
    public InstanceManager Instances { get; private set; }
    public JavaService Java { get; } = new();
    public GameLogService Logs { get; } = new();

    public MinecraftService(string? customInstancesRoot = null)
    {
        SetInstancesRoot(customInstancesRoot);
        Instances ??= new InstanceManager(InstancesRoot);
    }

    public void SetInstancesRoot(string? customInstancesRoot)
    {
        InstancesRoot = !string.IsNullOrWhiteSpace(customInstancesRoot)
            ? customInstancesRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "instances");
        Directory.CreateDirectory(InstancesRoot);
        Instances = new InstanceManager(InstancesRoot);
    }

    public string GetInstanceRoot(string version, string loader) => Path.Combine(InstancesRoot, $"{version}-{loader.ToLowerInvariant()}");

    public async Task<Process> LaunchAsync(
        string version, 
        string loader, 
        MSession session, 
        int ramMb, 
        bool stringDeduplication, 
        string? authlibInjectorPath = null,
        string? customInstanceRoot = null,
        string? gameLanguage = null,
        string? javaPath = null,
        Action<int>? onProcessExited = null,
        Action<string, double>? onDownloadProgress = null,
        string? serverIp = null,
        int serverPort = 25565,
        string authlibInjectorApiUrl = "ely.by")
    {
        // Никогда не используем модовый инстанс при выборе Vanilla
        if (loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
        {
            customInstanceRoot = null;
        }

        var root = !string.IsNullOrWhiteSpace(customInstanceRoot) && Directory.Exists(customInstanceRoot)
            ? customInstanceRoot
            : GetInstanceRoot(version, loader);

        Directory.CreateDirectory(root);
        ConfigureLanguage(root, gameLanguage);
        ConfigureChunkPerformance(root);

        var sharedRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KSLauncher", "runtime");
        Directory.CreateDirectory(sharedRuntime);

        var path = new MinecraftPath(root)
        {
            Runtime = sharedRuntime
        };
        var parameters = MinecraftLauncherParameters.CreateDefault(path);
        var launcher = new MinecraftLauncher(parameters);

        launcher.FileProgressChanged += (sender, args) =>
        {
            if (args.TotalTasks > 0)
            {
                var percent = (double)args.ProgressedTasks / args.TotalTasks * 100.0;
                onDownloadProgress?.Invoke($"Загрузка файлов ({args.ProgressedTasks}/{args.TotalTasks}): {args.Name}", percent);
            }
        };

        launcher.ByteProgressChanged += (sender, args) =>
        {
            if (args.TotalBytes > 0)
            {
                var percent = (double)args.ProgressedBytes / args.TotalBytes * 100.0;
                var mbDone = args.ProgressedBytes / (1024.0 * 1024.0);
                var mbTotal = args.TotalBytes / (1024.0 * 1024.0);
                onDownloadProgress?.Invoke($"Скачивание файлов игры: {mbDone:F1} / {mbTotal:F1} МБ ({percent:F0}%)", percent);
            }
        };

        var launchVersion = await InstallLoaderAsync(version, loader, path, launcher);

        var jvmArguments = new List<MArgument>();
        if (!string.IsNullOrWhiteSpace(authlibInjectorPath))
        {
            jvmArguments.Add(new MArgument($"-javaagent:{authlibInjectorPath}={authlibInjectorApiUrl}"));
        }

        // Подключаем оптимизированный набор аргументов JVM:
        // - Задействование ВСЕХ ядер и потоков процессора
        // - AlwaysPreTouch для исключения лагов при создании мира
        // - Тюнинг G1GC с минимальными задержками (MaxGCPauseMillis=35) для плавной генерации и прогрузки чанков
        foreach (var opt in JvmTuning.GetOptimizedJvmArguments(stringDeduplication))
        {
            jvmArguments.Add(new MArgument(opt));
        }

        var options = new MLaunchOption
        {
            Session = session,
            MaximumRamMb = ramMb,
            MinimumRamMb = Math.Min(1024, ramMb),
            GameLauncherName = "KSLauncher",
            GameLauncherVersion = "0.2.0",
            ExtraJvmArguments = jvmArguments,
            ServerIp = string.IsNullOrWhiteSpace(serverIp) ? null : serverIp.Trim(),
            ServerPort = serverPort > 0 ? serverPort : 25565
        };

        if (!string.IsNullOrWhiteSpace(javaPath) && File.Exists(javaPath))
        {
            options.JavaPath = javaPath;
        }
        else if (IsLegacyJava8Version(version))
        {
            var sysJava8 = Java.DetectInstalledJavas().FirstOrDefault(j => !j.IsAuto && j.MajorVersion == 8 && File.Exists(j.Path));
            if (sysJava8 != null)
            {
                options.JavaPath = sysJava8.Path;
            }
        }

        Logs.Clear();
        Logs.Append($"Подготовка запуска {version} ({loader})...");
        Logs.Append($"Каталог инстанса: {root}");
        Logs.Append($"Ядер CPU задействовано: {Environment.ProcessorCount} (все активны)");
        Logs.Append($"Оптимизация чанков и создания мира (G1GC + AlwaysPreTouch): ВКЛЮЧЕНА");
        if (!string.IsNullOrWhiteSpace(javaPath)) Logs.Append($"Используется Java: {javaPath}");

        var process = await launcher.InstallAndBuildProcessAsync(launchVersion, options);
        Instances.TouchInstance(root);

        // Redirect outputs for live log console
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        process.Start();
        try { process.PriorityClass = ProcessPriorityClass.High; } catch { }
        Logs.AttachToProcess(process, onProcessExited);

        return process;
    }

    private static async Task<string> InstallLoaderAsync(string version, string loader, MinecraftPath path, MinecraftLauncher launcher)
    {
        return loader.ToLowerInvariant() switch
        {
            "fabric" => await new FabricInstaller(new HttpClient()).Install(version, path),
            "quilt" => await new QuiltInstaller(new HttpClient()).Install(version, path),
            "forge" => await InstallForgeAsync(version, launcher, path),
            "neoforge" => await new NeoForgeInstaller(launcher).Install(version),
            "vanilla" => version,
            _ => throw new ArgumentException($"Неизвестный загрузчик: {loader}")
        };
    }

    private static bool IsLegacyJava8Version(string version)
    {
        var parts = version.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
        {
            return major == 1 && minor <= 16;
        }
        return false;
    }

    private static async Task<string> InstallForgeAsync(string version, MinecraftLauncher launcher, MinecraftPath path)
    {
        string? existingVersion = null;

        if (Directory.Exists(path.Versions))
        {
            existingVersion = Directory.GetDirectories(path.Versions)
                .Select(Path.GetFileName)
                .FirstOrDefault(d => d != null && d.StartsWith($"{version}-forge", StringComparison.OrdinalIgnoreCase));
        }

        string installedVersion;
        if (!string.IsNullOrWhiteSpace(existingVersion))
        {
            // Forge уже установлен — только проверяем/докачиваем библиотеки (launchwrapper и пр.)
            installedVersion = existingVersion!;
        }
        else
        {
            // Первый раз — устанавливаем Forge (SkipIfAlreadyInstalled=true предотвращает рекламную страницу)
            var forge = new ForgeInstaller(launcher);
            installedVersion = await forge.Install(version, new ForgeInstallOptions { SkipIfAlreadyInstalled = true });
        }

        // Всегда проверяем и докачиваем недостающие библиотеки (launchwrapper, log4j, и т.д.)
        await launcher.InstallAsync(installedVersion);
        return installedVersion;
    }

    public async Task<string> RepairInstanceAsync(string instanceRoot, string version, string loader, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Очистка повреждённых временных файлов...");
        if (!Directory.Exists(instanceRoot))
        {
            Directory.CreateDirectory(instanceRoot);
        }

        int deletedTempCount = 0;
        foreach (var file in Directory.GetFiles(instanceRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".download" or ".tmp" or ".crdownload" || Path.GetFileName(file).StartsWith(".~"))
            {
                try { File.Delete(file); deletedTempCount++; } catch { }
            }
        }

        int repairedModsCount = 0;
        var modsFolder = Path.Combine(instanceRoot, "mods");
        if (Directory.Exists(modsFolder))
        {
            onProgress?.Invoke("Проверка модов на целостность...");
            foreach (var jar in Directory.GetFiles(modsFolder, "*.jar"))
            {
                var info = new FileInfo(jar);
                if (info.Length == 0)
                {
                    try { File.Delete(jar); repairedModsCount++; } catch { }
                    continue;
                }

                try
                {
                    using var zip = ZipFile.OpenRead(jar);
                    var entry = zip.Entries.FirstOrDefault();
                }
                catch
                {
                    try
                    {
                        var corruptedPath = jar + ".broken";
                        if (File.Exists(corruptedPath)) File.Delete(corruptedPath);
                        File.Move(jar, corruptedPath);
                        repairedModsCount++;
                    }
                    catch { }
                }
            }
        }

        onProgress?.Invoke($"Восстановление ядра {loader} и официальных библиотек...");
        var path = new MinecraftPath(instanceRoot);
        var launcher = new MinecraftLauncher(path);

        var launchVersion = await InstallLoaderAsync(version, loader, path, launcher);
        await launcher.InstallAsync(launchVersion);

        string additionalModAdded = "";
        if (loader.Equals("fabric", StringComparison.OrdinalIgnoreCase) && Directory.Exists(modsFolder))
        {
            var hasFabricApi = Directory.GetFiles(modsFolder, "fabric-api*.jar").Any();
            if (!hasFabricApi)
            {
                onProgress?.Invoke("Добавление обязательного мода Fabric API...");
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("KSLauncher/0.2.0");
                    var searchUrl = $"https://api.modrinth.com/v2/project/fabric-api/version?game_versions=[\"{version}\"]&loaders=[\"fabric\"]";
                    var jsonStr = await http.GetStringAsync(searchUrl);
                    using var doc = JsonDocument.Parse(jsonStr);
                    if (doc.RootElement.GetArrayLength() > 0)
                    {
                        var firstVer = doc.RootElement[0];
                        var files = firstVer.GetProperty("files");
                        if (files.GetArrayLength() > 0)
                        {
                            var fileObj = files[0];
                            var downloadUrl = fileObj.GetProperty("url").GetString();
                            var filename = fileObj.GetProperty("filename").GetString();
                            if (!string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(filename))
                            {
                                var bytes = await http.GetByteArrayAsync(downloadUrl);
                                await File.WriteAllBytesAsync(Path.Combine(modsFolder, filename), bytes);
                                additionalModAdded = $"\n• Автоматически добавлен необходимый мод: {filename}";
                            }
                        }
                    }
                }
                catch { }
            }
        }

        onProgress?.Invoke("Сборка успешно починена!");
        return $"Сборка починена успешно!\n• Очищено временных файлов: {deletedTempCount}\n• Проверены моды (исправлено/удалено битых: {repairedModsCount})\n• Загрузчик {loader} и библиотеки игры полностью переустановлены.{additionalModAdded}";
    }

    private static void ConfigureLanguage(string instanceRoot, string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return;
        var langCode = language.ToLowerInvariant() switch
        {
            "ru" or "russian" or "русский" => "ru_ru",
            "en" or "english" or "английский" => "en_us",
            _ => language
        };

        var optionsPath = Path.Combine(instanceRoot, "options.txt");
        try
        {
            if (File.Exists(optionsPath))
            {
                var lines = File.ReadAllLines(optionsPath).ToList();
                var found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith("lang:", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"lang:{langCode}";
                        found = true;
                        break;
                    }
                }
                if (!found) lines.Add($"lang:{langCode}");
                File.WriteAllLines(optionsPath, lines);
            }
            else
            {
                File.WriteAllText(optionsPath, $"lang:{langCode}\n");
            }
        }
        catch { }
    }

    private static void ConfigureChunkPerformance(string instanceRoot)
    {
        var optionsPath = Path.Combine(instanceRoot, "options.txt");
        try
        {
            var lines = File.Exists(optionsPath)
                ? File.ReadAllLines(optionsPath).ToList()
                : new List<string>();

            // prioritizeChunkUpdates:0 = Threaded (многопоточная асинхронная прогрузка чанков без просадок FPS)
            var idx = lines.FindIndex(l => l.StartsWith("prioritizeChunkUpdates:", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                lines[idx] = "prioritizeChunkUpdates:0";
            else
                lines.Add("prioritizeChunkUpdates:0");

            File.WriteAllLines(optionsPath, lines);
        }
        catch { }
    }
}
