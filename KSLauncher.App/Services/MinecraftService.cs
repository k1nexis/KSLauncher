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
using System.Net.Http;

namespace KSLauncher.App.Services;

public sealed class MinecraftService
{
    public string InstancesRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "instances");

    public MinecraftService()
    {
        Directory.CreateDirectory(InstancesRoot);
    }

    public string GetInstanceRoot(string version, string loader) => Path.Combine(InstancesRoot, $"{version}-{loader.ToLowerInvariant()}");

    public async Task LaunchAsync(string version, string loader, MSession session, int ramMb, bool stringDeduplication, string? authlibInjectorPath = null)
    {
        var root = GetInstanceRoot(version, loader);
        Directory.CreateDirectory(root);
        var path = new MinecraftPath(root);
        var launcher = new MinecraftLauncher(path);
        var launchVersion = version;

        switch (loader.ToLowerInvariant())
        {
            case "fabric":
                launchVersion = await new FabricInstaller(new HttpClient()).Install(version, path);
                break;
            case "quilt":
                launchVersion = await new QuiltInstaller(new HttpClient()).Install(version, path);
                break;
            case "forge":
                launchVersion = await new ForgeInstaller(launcher).Install(version, new ForgeInstallOptions());
                await launcher.InstallAsync(launchVersion);
                break;
            case "neoforge":
                launchVersion = await new NeoForgeInstaller(launcher).Install(version);
                break;
            case "vanilla":
                break;
            default:
                throw new ArgumentException($"Неизвестный загрузчик: {loader}");
        }
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
            GameLauncherVersion = "0.1.0",
            ExtraJvmArguments = jvmArguments
        };

        var process = await launcher.InstallAndBuildProcessAsync(launchVersion, options);
        process.Start();
    }
}
