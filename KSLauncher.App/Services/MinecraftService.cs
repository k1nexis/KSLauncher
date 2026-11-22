extern alias CmlForge;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using ForgeInstaller = CmlForge::CmlLib.Core.Installer.Forge.ForgeInstaller;
using ForgeInstallOptions = CmlForge::CmlLib.Core.Installer.Forge.ForgeInstallOptions;
using NeoForgeInstaller = CmlLib.Core.Installer.NeoForge.NeoForgeInstaller;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace KSLauncher.App.Services;

public sealed class MinecraftService
{
    public string InstancesRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "instances");
    public InstanceManager Instances { get; }

    public MinecraftService()
    {
        Directory.CreateDirectory(InstancesRoot);
        Instances = new InstanceManager(InstancesRoot);
    }

    public string GetInstanceRoot(string version, string loader) => Path.Combine(InstancesRoot, $"{version}-{loader.ToLowerInvariant()}");

    public async Task LaunchAsync(
        string version, 
        string loader, 
        MSession session, 
        int ramMb, 
        bool stringDeduplication, 
        string? authlibInjectorPath = null,
        string? customInstanceRoot = null,
        string? gameLanguage = null)
    {
        var root = !string.IsNullOrWhiteSpace(customInstanceRoot) && Directory.Exists(customInstanceRoot)
            ? customInstanceRoot
            : GetInstanceRoot(version, loader);

        Directory.CreateDirectory(root);
        ConfigureLanguage(root, gameLanguage);

        var path = new MinecraftPath(root);
        var launcher = new MinecraftLauncher(path);
        var launchVersion = await InstallLoaderAsync(version, loader, path, launcher);

        var jvmArguments = new List<MArgument>();
        if (!string.IsNullOrWhiteSpace(authlibInjectorPath)) jvmArguments.Add(new MArgument($"-javaagent:{authlibInjectorPath}=ely.by"));
        jvmArguments.Add(new MArgument("-XX:+UseG1GC"));
        if (stringDeduplication) jvmArguments.Add(new MArgument("-XX:+UseStringDeduplication"));
        var options = new MLaunchOption
        {
            Session = session,
            MaximumRamMb = ramMb,
            MinimumRamMb = Math.Min(1024, ramMb),
            GameLauncherName = "KSLauncher",
            GameLauncherVersion = "0.2.0",
            ExtraJvmArguments = jvmArguments
        };

        var process = await launcher.InstallAndBuildProcessAsync(launchVersion, options);
        Instances.TouchInstance(root);
        process.Start();
    }

    private static async Task<string> InstallLoaderAsync(string version, string loader, MinecraftPath path, MinecraftLauncher launcher)
    {
        return loader.ToLowerInvariant() switch
        {
            "fabric" => await new FabricInstaller(new HttpClient()).Install(version, path),
            "quilt" => await new QuiltInstaller(new HttpClient()).Install(version, path),
            "forge" => await InstallForgeAsync(version, launcher),
            "neoforge" => await new NeoForgeInstaller(launcher).Install(version),
            "vanilla" => version,
            _ => throw new ArgumentException($"Неизвестный загрузчик: {loader}")
        };
    }

    private static async Task<string> InstallForgeAsync(string version, MinecraftLauncher launcher)
    {
        var forge = new ForgeInstaller(launcher);
        var installedVersion = await forge.Install(version, new ForgeInstallOptions());
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

        // 1. Clean temp download files
        int deletedTempCount = 0;
        foreach (var file in Directory.GetFiles(instanceRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".download" or ".tmp" or ".crdownload" || Path.GetFileName(file).StartsWith(".~"))
            {
                try { File.Delete(file); deletedTempCount++; } catch { }
            }
        }

        // 2. Validate mods folder for 0-byte or corrupted archives
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

                // Check if zip is readable
                try
                {
                    using var zip = ZipFile.OpenRead(jar);
                    var entry = zip.Entries.FirstOrDefault();
                }
                catch
                {
                    // Corrupted jar
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

        // 3. Reinstall loader and game core
        onProgress?.Invoke($"Восстановление ядра {loader} и официальных библиотек...");
        var path = new MinecraftPath(instanceRoot);
        var launcher = new MinecraftLauncher(path);

        var launchVersion = await InstallLoaderAsync(version, loader, path, launcher);
        await launcher.InstallAsync(launchVersion);

        onProgress?.Invoke("Сборка успешно починена!");
        return $"Сборка починена успешно!\n• Очищено временных файлов: {deletedTempCount}\n• Проверены моды (исправлено/удалено битых: {repairedModsCount})\n• Загрузчик {loader} и библиотеки игры полностью переустановлены.";
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
}
