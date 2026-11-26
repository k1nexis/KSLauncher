using System.Net.Http.Headers;
using System.Net.Http;
using System.Text.Json;
using System.IO;

namespace KSLauncher.App.Services;

public sealed record CurseForgeProject(
    int Id,
    string Slug,
    string Title,
    string Description,
    long Downloads,
    string ProjectType
);

public sealed record CurseForgeInstallResult(
    string FileName,
    IReadOnlyList<string> Dependencies,
    string TargetDirectory
);

public sealed class CurseForgeService
{
    private static readonly HttpClient Client = CreateClient();
    private const string DefaultApiKey = "$2a$10$bL4bIL5pUWqfcO7KQtnMReakwtfHbNKh6v1uTpKlzhwoueEJQnPnm";
    private const int GameIdMinecraft = 432;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.curseforge.com/v1/"),
            Timeout = TimeSpan.FromSeconds(35)
        };
        client.DefaultRequestHeaders.Add("x-api-key", DefaultApiKey);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KSLauncher", "0.2.0"));
        return client;
    }

    public async Task<IReadOnlyList<CurseForgeProject>> SearchAsync(
        string query, 
        string projectType, 
        string loader, 
        string gameVersion, 
        CancellationToken cancellationToken = default)
    {
        int classId = projectType switch
        {
            "mod" => 6,
            "modpack" => 4471,
            "resourcepack" => 12,
            "shader" => 6552,
            _ => 6
        };

        int loaderType = loader.ToLowerInvariant() switch
        {
            "forge" => 1,
            "fabric" => 4,
            "quilt" => 5,
            "neoforge" => 6,
            _ => 0
        };

        var url = $"mods/search?gameId={GameIdMinecraft}&classId={classId}&pageSize=30&sortField=2&sortOrder=desc";
        if (!string.IsNullOrWhiteSpace(query)) url += $"&searchFilter={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(gameVersion)) url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";
        if (loaderType > 0 && classId == 6) url += $"&modLoaderType={loaderType}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var list = new List<CurseForgeProject>();

        if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("id").GetInt32();
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown";
                var summary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                var slug = item.TryGetProperty("slug", out var sl) ? sl.GetString() ?? id.ToString() : id.ToString();
                var downloads = item.TryGetProperty("downloadCount", out var d) ? (long)d.GetDouble() : 0;

                list.Add(new CurseForgeProject(id, slug, name, summary, downloads, projectType));
            }
        }

        return list;
    }

    public async Task<CurseForgeInstallResult> InstallAsync(
        CurseForgeProject project, 
        string projectType, 
        string loader, 
        string gameVersion, 
        string instancesRoot, 
        CancellationToken cancellationToken = default)
    {
        var instance = (File.Exists(Path.Combine(instancesRoot, "instance.json")) || Directory.Exists(Path.Combine(instancesRoot, "mods")))
            ? instancesRoot
            : Path.Combine(instancesRoot, SafeSegment($"{gameVersion}-{loader.ToLowerInvariant()}"));
        var subFolder = projectType switch
        {
            "mod" => "mods",
            "resourcepack" => "resourcepacks",
            "shader" => "shaderpacks",
            _ => "modpacks"
        };
        var targetDir = Path.Combine(instance, subFolder);
        Directory.CreateDirectory(targetDir);

        var installedIds = new HashSet<int>();
        var dependenciesList = new List<string>();

        var primaryFileName = await DownloadModWithDependenciesAsync(
            project.Id,
            project.Title,
            projectType,
            loader,
            gameVersion,
            targetDir,
            installedIds,
            dependenciesList,
            cancellationToken
        );

        return new CurseForgeInstallResult(primaryFileName, dependenciesList, targetDir);
    }

    public async Task<string> DownloadModWithDependenciesAsync(
        int modId,
        string modTitle,
        string projectType,
        string loader,
        string gameVersion,
        string targetDir,
        HashSet<int> installedMods,
        List<string> downloadedDependencies,
        CancellationToken ct)
    {
        if (!installedMods.Add(modId)) return $"{modTitle}.jar";

        int loaderType = loader.ToLowerInvariant() switch
        {
            "forge" => 1,
            "fabric" => 4,
            "quilt" => 5,
            "neoforge" => 6,
            _ => 0
        };

        var filesUrl = $"mods/{modId}/files?pageSize=20";
        if (!string.IsNullOrWhiteSpace(gameVersion)) filesUrl += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";
        if (loaderType > 0 && projectType == "mod") filesUrl += $"&modLoaderType={loaderType}";

        using var response = await Client.GetAsync(filesUrl, ct);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (!json.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"На CurseForge не найден совместимый файл для {modTitle} (Minecraft {gameVersion} / {loader}).");
        }

        var fileItem = data.EnumerateArray().First();
        var fileName = SafeFileName(fileItem.GetProperty("fileName").GetString() ?? $"{modTitle}.jar");
        var downloadUrl = fileItem.TryGetProperty("downloadUrl", out var dl) ? dl.GetString() : null;

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            var fileId = fileItem.GetProperty("id").GetInt32();
            var part1 = fileId / 1000;
            var part2 = fileId % 1000;
            downloadUrl = $"https://edge.forgecdn.net/files/{part1}/{part2}/{Uri.EscapeDataString(fileName)}";
        }

        // Рекурсивное скачивание зависимостей (relationType == 3: RequiredDependency)
        if (projectType == "mod" && fileItem.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
        {
            foreach (var dep in deps.EnumerateArray())
            {
                var relType = dep.TryGetProperty("relationType", out var rt) ? rt.GetInt32() : 0;
                var depModId = dep.TryGetProperty("modId", out var mid) ? mid.GetInt32() : 0;

                if (relType == 3 && depModId > 0 && !installedMods.Contains(depModId))
                {
                    try
                    {
                        var depProject = await GetProjectByIdAsync(depModId, ct);
                        if (depProject != null)
                        {
                            downloadedDependencies.Add(depProject.Title);
                            await DownloadModWithDependenciesAsync(
                                depProject.Id,
                                depProject.Title,
                                "mod",
                                loader,
                                gameVersion,
                                targetDir,
                                installedMods,
                                downloadedDependencies,
                                ct
                            );
                        }
                    }
                    catch { }
                }
            }
        }

        var destination = Path.Combine(targetDir, fileName);
        var temp = destination + ".download";

        using var downloadRes = await Client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        downloadRes.EnsureSuccessStatusCode();

        await using (var src = await downloadRes.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await src.CopyToAsync(dst, ct);
        }

        if (File.Exists(destination)) File.Delete(destination);
        File.Move(temp, destination);

        return fileName;
    }

    private async Task<CurseForgeProject?> GetProjectByIdAsync(int modId, CancellationToken ct)
    {
        try
        {
            using var response = await Client.GetAsync($"mods/{modId}", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (!json.RootElement.TryGetProperty("data", out var data)) return null;

            var id = data.GetProperty("id").GetInt32();
            var name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown";
            var summary = data.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
            var slug = data.TryGetProperty("slug", out var sl) ? sl.GetString() ?? id.ToString() : id.ToString();
            var downloads = data.TryGetProperty("downloadCount", out var d) ? (long)d.GetDouble() : 0;
            return new CurseForgeProject(id, slug, name, summary, downloads, "mod");
        }
        catch { return null; }
    }

    private static string SafeSegment(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c));
    private static string SafeFileName(string value)
    {
        var name = Path.GetFileName(value.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return "download.jar";
        return name;
    }
}
