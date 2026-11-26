using System.IO;
using System.Text.Json;
using System.Windows;

namespace KSLauncher.App.Services;

public sealed record GameInstanceInfo(
    string Id,
    string Name,
    string Version,
    string Loader,
    string FolderPath,
    int ModsCount,
    bool IsInstalled,
    DateTime LastPlayed,
    bool HasModError = false
)
{
    public Visibility RepairVisibility => HasModError ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class InstanceMetadata
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Loader { get; set; } = "vanilla";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastPlayed { get; set; } = DateTime.UtcNow;
}

public sealed class InstanceManager
{
    private readonly string _instancesRoot;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public InstanceManager(string instancesRoot)
    {
        _instancesRoot = instancesRoot;
        Directory.CreateDirectory(_instancesRoot);
    }

    public IReadOnlyList<GameInstanceInfo> GetInstances()
    {
        Directory.CreateDirectory(_instancesRoot);
        var dirs = Directory.GetDirectories(_instancesRoot);
        var list = new List<GameInstanceInfo>();

        foreach (var dir in dirs)
        {
            var dirName = Path.GetFileName(dir);
            var metaFile = Path.Combine(dir, "instance.json");
            string name = dirName;
            string version = "Unknown";
            string loader = "vanilla";
            DateTime lastPlayed = Directory.GetLastWriteTime(dir);

            if (File.Exists(metaFile))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<InstanceMetadata>(File.ReadAllText(metaFile));
                    if (meta != null)
                    {
                        name = !string.IsNullOrWhiteSpace(meta.Name) ? meta.Name : dirName;
                        version = meta.Version;
                        loader = meta.Loader;
                        lastPlayed = meta.LastPlayed;
                    }
                }
                catch { }
            }
            else
            {
                // Fallback parsing from dirName like "1.20.4-fabric"
                var parts = dirName.Split('-', 2);
                if (parts.Length == 2)
                {
                    version = parts[0];
                    loader = parts[1];
                    name = $"{version} ({char.ToUpperInvariant(loader[0]) + loader[1..]})";
                }
                else
                {
                    version = dirName;
                }
            }

            var modsDir = Path.Combine(dir, "mods");
            var modsCount = Directory.Exists(modsDir)
                ? Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly).Length
                : 0;

            var isInstalled = Directory.Exists(Path.Combine(dir, "versions")) || Directory.Exists(modsDir) || File.Exists(Path.Combine(dir, "options.txt"));

            bool hasModError = false;
            var crashReportsDir = Path.Combine(dir, "crash-reports");
            if (Directory.Exists(crashReportsDir))
            {
                var crashFiles = Directory.GetFiles(crashReportsDir, "crash-*.txt");
                if (crashFiles.Length > 0)
                {
                    hasModError = true;
                }
            }
            if (!hasModError)
            {
                var latestLog = Path.Combine(dir, "logs", "latest.log");
                if (File.Exists(latestLog))
                {
                    try
                    {
                        using var fs = new FileStream(latestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var len = fs.Length;
                        var offset = Math.Max(0, len - 32768);
                        fs.Seek(offset, SeekOrigin.Begin);
                        using var reader = new StreamReader(fs);
                        var tail = reader.ReadToEnd();
                        if (tail.Contains("net.fabricmc.loader") ||
                            tail.Contains("ModResolutionException") ||
                            tail.Contains("Incompatible mods") ||
                            tail.Contains("Missing or unsupported mandatory dependencies") ||
                            tail.Contains("DuplicateModsException") ||
                            tail.Contains("A critical error occurred"))
                        {
                            hasModError = true;
                        }
                    }
                    catch { }
                }
            }

            list.Add(new GameInstanceInfo(
                Id: dirName,
                Name: name,
                Version: version,
                Loader: loader.ToLowerInvariant(),
                FolderPath: dir,
                ModsCount: modsCount,
                IsInstalled: isInstalled,
                LastPlayed: lastPlayed,
                HasModError: hasModError
            ));
        }

        return list.OrderByDescending(x => x.LastPlayed).ToArray();
    }

    public GameInstanceInfo CreateInstance(string name, string version, string loader)
    {
        var cleanLoader = loader.Trim().ToLowerInvariant();
        var safePrefix = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ' ' ? '_' : c)).Trim('_');
        if (string.IsNullOrWhiteSpace(safePrefix)) safePrefix = "custom";

        var id = $"{safePrefix}-{version}-{cleanLoader}";
        var folderPath = Path.Combine(_instancesRoot, id);
        int counter = 1;
        while (Directory.Exists(folderPath))
        {
            id = $"{safePrefix}-{counter}-{version}-{cleanLoader}";
            folderPath = Path.Combine(_instancesRoot, id);
            counter++;
        }

        Directory.CreateDirectory(folderPath);
        Directory.CreateDirectory(Path.Combine(folderPath, "mods"));

        var meta = new InstanceMetadata
        {
            Id = id,
            Name = name.Trim(),
            Version = version,
            Loader = cleanLoader,
            CreatedAt = DateTime.UtcNow,
            LastPlayed = DateTime.UtcNow
        };

        File.WriteAllText(Path.Combine(folderPath, "instance.json"), JsonSerializer.Serialize(meta, JsonOptions));

        return new GameInstanceInfo(
            Id: id,
            Name: meta.Name,
            Version: meta.Version,
            Loader: meta.Loader,
            FolderPath: folderPath,
            ModsCount: 0,
            IsInstalled: true,
            LastPlayed: meta.LastPlayed
        );
    }

    public void UpdateInstanceName(string instancePath, string newName)
    {
        var metaFile = Path.Combine(instancePath, "instance.json");
        if (File.Exists(metaFile))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<InstanceMetadata>(File.ReadAllText(metaFile));
                if (meta != null)
                {
                    meta.Name = newName.Trim();
                    File.WriteAllText(metaFile, JsonSerializer.Serialize(meta, JsonOptions));
                }
            }
            catch { }
        }
        else
        {
            var dirName = Path.GetFileName(instancePath);
            var meta = new InstanceMetadata
            {
                Id = dirName,
                Name = newName.Trim(),
                CreatedAt = DateTime.UtcNow,
                LastPlayed = DateTime.UtcNow
            };
            File.WriteAllText(metaFile, JsonSerializer.Serialize(meta, JsonOptions));
        }
    }

    public void TouchInstance(string instancePath)
    {
        var metaFile = Path.Combine(instancePath, "instance.json");
        if (File.Exists(metaFile))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<InstanceMetadata>(File.ReadAllText(metaFile));
                if (meta != null)
                {
                    meta.LastPlayed = DateTime.UtcNow;
                    File.WriteAllText(metaFile, JsonSerializer.Serialize(meta, JsonOptions));
                }
            }
            catch { }
        }
    }

    public void DeleteInstance(string folderPath)
    {
        if (Directory.Exists(folderPath))
        {
            Directory.Delete(folderPath, recursive: true);
        }
    }
}
