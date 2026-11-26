using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KSLauncher.App.Services;

public sealed class LocalServerConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Мой локальный сервер";
    public string Version { get; set; } = "1.20.4";
    public string Core { get; set; } = "vanilla"; // vanilla | paper | fabric
    public int RamMb { get; set; } = 2048;
    public int Port { get; set; } = 25565;
    public bool OnlineMode { get; set; } = false; // false = Пиратский (Offline/Ely/KS), true = Лицензия
    public string Motd { get; set; } = "Локальный сервер KS Launcher";
    public int MaxPlayers { get; set; } = 20;
    public string Difficulty { get; set; } = "normal";
    public string Gamemode { get; set; } = "survival";
    public bool Pvp { get; set; } = true;
    public bool WhiteList { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string FolderPath { get; set; } = "";

    public string ModeBadge => OnlineMode ? "Лицензия" : "Пиратский";
    public string Summary => $"{Core.ToUpperInvariant()} {Version} • {RamMb / 1024} ГБ ОЗУ • Порт {Port} • {ModeBadge}";
}

public sealed class LocalServerService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _serversRoot;
    private readonly StringBuilder _consoleBuffer = new();
    private readonly object _consoleLock = new();

    private Process? _runningProcess;
    private LocalServerConfig? _runningServer;

    public event Action<string>? ConsoleLineReceived;
    public event Action? ServerStateChanged;

    public bool IsRunning => _runningProcess != null && !_runningProcess.HasExited;
    public LocalServerConfig? RunningServer => IsRunning ? _runningServer : null;

    static LocalServerService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("KSLauncher/0.2.0");
    }

    public LocalServerService()
    {
        _serversRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "servers");
        Directory.CreateDirectory(_serversRoot);
    }

    public IReadOnlyList<LocalServerConfig> GetServers()
    {
        Directory.CreateDirectory(_serversRoot);
        var list = new List<LocalServerConfig>();
        foreach (var dir in Directory.GetDirectories(_serversRoot))
        {
            var metaPath = Path.Combine(dir, "ks-server.json");
            if (!File.Exists(metaPath)) continue;
            try
            {
                var cfg = JsonSerializer.Deserialize<LocalServerConfig>(File.ReadAllText(metaPath));
                if (cfg != null)
                {
                    cfg.FolderPath = dir;
                    list.Add(cfg);
                }
            }
            catch { }
        }
        return list.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public LocalServerConfig CreateServer(
        string name,
        string version,
        string core,
        int ramMb,
        int port,
        bool onlineMode,
        string motd,
        int maxPlayers,
        string difficulty,
        string gamemode,
        bool pvp,
        bool whiteList)
    {
        var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c)).Trim('_');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "server";

        var id = $"{safeName}-{version}-{core.ToLowerInvariant()}";
        var dir = Path.Combine(_serversRoot, id);
        int i = 1;
        while (Directory.Exists(dir))
        {
            id = $"{safeName}-{i}-{version}-{core.ToLowerInvariant()}";
            dir = Path.Combine(_serversRoot, id);
            i++;
        }

        Directory.CreateDirectory(dir);
        var cfg = new LocalServerConfig
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? $"Сервер {version}" : name.Trim(),
            Version = version.Trim(),
            Core = core.Trim().ToLowerInvariant(),
            RamMb = Math.Clamp(ramMb, 1024, 32768),
            Port = port is > 0 and <= 65535 ? port : 25565,
            OnlineMode = onlineMode,
            Motd = string.IsNullOrWhiteSpace(motd) ? "Локальный сервер KS Launcher" : motd.Trim(),
            MaxPlayers = Math.Clamp(maxPlayers, 1, 200),
            Difficulty = difficulty,
            Gamemode = gamemode,
            Pvp = pvp,
            WhiteList = whiteList,
            CreatedAt = DateTime.UtcNow,
            FolderPath = dir
        };

        SaveServerConfig(cfg);
        WriteServerFiles(cfg);
        return cfg;
    }

    public void SaveServerConfig(LocalServerConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.FolderPath))
            cfg.FolderPath = Path.Combine(_serversRoot, cfg.Id);
        Directory.CreateDirectory(cfg.FolderPath);
        File.WriteAllText(Path.Combine(cfg.FolderPath, "ks-server.json"), JsonSerializer.Serialize(cfg, JsonOptions));
        WriteServerFiles(cfg);
    }

    public void DeleteServer(LocalServerConfig cfg)
    {
        if (IsRunning && _runningServer?.Id == cfg.Id)
            throw new InvalidOperationException("Сначала остановите запущенный сервер.");
        if (Directory.Exists(cfg.FolderPath))
            Directory.Delete(cfg.FolderPath, recursive: true);
    }

    private static void WriteServerFiles(LocalServerConfig cfg)
    {
        Directory.CreateDirectory(cfg.FolderPath);
        File.WriteAllText(Path.Combine(cfg.FolderPath, "eula.txt"), "# Accepted via KS Launcher\neula=true\n");

        var propsPath = Path.Combine(cfg.FolderPath, "server.properties");
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(propsPath))
        {
            foreach (var line in File.ReadAllLines(propsPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq > 0)
                    props[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
            }
        }

        props["server-port"] = cfg.Port.ToString();
        props["online-mode"] = cfg.OnlineMode ? "true" : "false";
        props["motd"] = cfg.Motd;
        props["max-players"] = cfg.MaxPlayers.ToString();
        props["difficulty"] = cfg.Difficulty;
        props["gamemode"] = cfg.Gamemode;
        props["pvp"] = cfg.Pvp ? "true" : "false";
        props["white-list"] = cfg.WhiteList ? "true" : "false";
        props["enable-command-block"] = "true";

        var sb = new StringBuilder();
        sb.AppendLine("# Minecraft server properties (managed by KS Launcher)");
        foreach (var kv in props)
            sb.AppendLine($"{kv.Key}={kv.Value}");
        File.WriteAllText(propsPath, sb.ToString());
    }

    public async Task EnsureServerJarAsync(LocalServerConfig cfg, Action<string>? onStatus = null, CancellationToken ct = default)
    {
        var jarPath = Path.Combine(cfg.FolderPath, "server.jar");
        if (File.Exists(jarPath) && new FileInfo(jarPath).Length > 100_000)
            return;

        onStatus?.Invoke($"Скачивание ядра {cfg.Core} для {cfg.Version}...");
        AppendConsole($"[KS Server] Загрузка ядра {cfg.Core.ToUpperInvariant()} {cfg.Version}...");

        string downloadUrl = cfg.Core.ToLowerInvariant() switch
        {
            "paper" => await ResolvePaperServerUrlAsync(cfg.Version, ct),
            "fabric" => await ResolveFabricServerUrlAsync(cfg.Version, ct),
            _ => await ResolveVanillaServerUrlAsync(cfg.Version, ct)
        };

        var bytes = await Http.GetByteArrayAsync(downloadUrl, ct);
        await File.WriteAllBytesAsync(jarPath, bytes, ct);
        AppendConsole($"[KS Server] Ядро server.jar загружено ({bytes.Length / (1024.0 * 1024.0):F1} МБ).");
    }

    private static async Task<string> ResolveVanillaServerUrlAsync(string version, CancellationToken ct)
    {
        var manifestStr = await Http.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
        using var manifest = JsonDocument.Parse(manifestStr);
        string? versionUrl = null;
        foreach (var item in manifest.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == version)
            {
                versionUrl = item.GetProperty("url").GetString();
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(versionUrl))
            throw new InvalidOperationException($"Версия Vanilla {version} не найдена в манифесте Mojang.");

        var verStr = await Http.GetStringAsync(versionUrl, ct);
        using var verDoc = JsonDocument.Parse(verStr);
        if (!verDoc.RootElement.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("server", out var serverObj) ||
            !serverObj.TryGetProperty("url", out var urlProp))
        {
            throw new InvalidOperationException($"Для версии {version} отсутствует официальный server.jar.");
        }
        return urlProp.GetString()!;
    }

    private static async Task<string> ResolvePaperServerUrlAsync(string version, CancellationToken ct)
    {
        var buildsStr = await Http.GetStringAsync($"https://api.papermc.io/v2/projects/paper/versions/{version}/builds", ct);
        using var doc = JsonDocument.Parse(buildsStr);
        var builds = doc.RootElement.GetProperty("builds");
        if (builds.GetArrayLength() == 0)
            throw new InvalidOperationException($"Paper для версии {version} не найден.");

        var latest = builds[builds.GetArrayLength() - 1];
        var buildNum = latest.GetProperty("build").GetInt32();
        var fileName = latest.GetProperty("downloads").GetProperty("application").GetProperty("name").GetString();
        return $"https://api.papermc.io/v2/projects/paper/versions/{version}/builds/{buildNum}/downloads/{fileName}";
    }

    private static async Task<string> ResolveFabricServerUrlAsync(string version, CancellationToken ct)
    {
        var loadersStr = await Http.GetStringAsync($"https://meta.fabricmc.net/v2/versions/loader/{version}", ct);
        using var loadersDoc = JsonDocument.Parse(loadersStr);
        if (loadersDoc.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException($"Fabric для версии {version} не найден.");
        var loaderVer = loadersDoc.RootElement[0].GetProperty("loader").GetProperty("version").GetString();

        var instStr = await Http.GetStringAsync("https://meta.fabricmc.net/v2/versions/installer", ct);
        using var instDoc = JsonDocument.Parse(instStr);
        var instVer = instDoc.RootElement[0].GetProperty("version").GetString();

        return $"https://meta.fabricmc.net/v2/versions/loader/{version}/{loaderVer}/{instVer}/server/jar";
    }

    public async Task StartServerAsync(LocalServerConfig cfg, string? javaExePath, Action<string>? onStatus = null, CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("Другой локальный сервер уже запущен.");

        ClearConsole();
        SaveServerConfig(cfg);
        await EnsureServerJarAsync(cfg, onStatus, ct);

        var javaBin = !string.IsNullOrWhiteSpace(javaExePath) && File.Exists(javaExePath)
            ? javaExePath
            : "java";

        // Prefer console java.exe if user selected javaw.exe
        if (javaBin.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            var consoleJava = Path.Combine(Path.GetDirectoryName(javaBin)!, "java.exe");
            if (File.Exists(consoleJava)) javaBin = consoleJava;
        }

        var minRam = Math.Min(1024, cfg.RamMb);
        var psi = new ProcessStartInfo
        {
            FileName = javaBin,
            Arguments = $"-Xms{minRam}M -Xmx{cfg.RamMb}M -jar server.jar nogui",
            WorkingDirectory = cfg.FolderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) AppendConsole(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) AppendConsole("[ERR] " + e.Data);
        };
        process.Exited += (_, _) =>
        {
            AppendConsole($"[KS Server] Сервер остановлен (код {process.ExitCode}).");
            _runningProcess = null;
            _runningServer = null;
            ServerStateChanged?.Invoke();
        };

        AppendConsole($"[KS Server] Запуск «{cfg.Name}» ({cfg.Core} {cfg.Version}, {cfg.RamMb} МБ ОЗУ, {cfg.ModeBadge})...");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _runningProcess = process;
        _runningServer = cfg;
        ServerStateChanged?.Invoke();
    }

    public void SendCommand(string command)
    {
        if (!IsRunning || _runningProcess == null || string.IsNullOrWhiteSpace(command)) return;
        try
        {
            AppendConsole($"> {command}");
            _runningProcess.StandardInput.WriteLine(command);
            _runningProcess.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            AppendConsole($"[KS Server] Не удалось отправить команду: {ex.Message}");
        }
    }

    public async Task StopServerAsync()
    {
        var proc = _runningProcess;
        if (proc == null || proc.HasExited) return;

        AppendConsole("[KS Server] Отправка команды stop...");
        try
        {
            proc.StandardInput.WriteLine("stop");
            proc.StandardInput.Flush();
        }
        catch { }

        var exited = await Task.Run(() => proc.WaitForExit(8000));
        if (!exited && !proc.HasExited)
        {
            AppendConsole("[KS Server] Принудительное завершение процесса...");
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    public string GetConsoleText()
    {
        lock (_consoleLock) return _consoleBuffer.ToString();
    }

    public void ClearConsole()
    {
        lock (_consoleLock) _consoleBuffer.Clear();
    }

    private void AppendConsole(string line)
    {
        lock (_consoleLock)
        {
            if (_consoleBuffer.Length > 120_000)
                _consoleBuffer.Remove(0, 40_000);
            _consoleBuffer.AppendLine(line);
        }
        ConsoleLineReceived?.Invoke(line);
    }

    public void Dispose()
    {
        try
        {
            if (_runningProcess != null && !_runningProcess.HasExited)
                _runningProcess.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
