using System.Net.Http.Headers;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.IO.Compression;

namespace KSLauncher.App.Services;

public sealed record ModrinthProject(string ProjectId, string Slug, string Title, string Description, long Downloads, string ProjectType);
public sealed record ModrinthInstallResult(string FileName, IReadOnlyList<string> Dependencies, string TargetDirectory);

/// <summary>Modrinth search, safe client-side downloads and modpack extractor into a launch profile.</summary>
public sealed class ModrinthService
{
    private static readonly HttpClient Client = CreateClient();
    private const int MaxDownloadBytes = 512 * 1024 * 1024;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.modrinth.com/v2/"), Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KSLauncher", "0.2.0"));
        return client;
    }

    public async Task<IReadOnlyList<ModrinthProject>> SearchAsync(string query, string projectType, string loader, string gameVersion, CancellationToken cancellationToken = default)
    {
        var facets = new List<string[]> { new[] { $"project_type:{projectType}" } };
        if (projectType == "mod" && loader is not ("Any" or "vanilla")) facets.Add(new[] { $"categories:{loader.ToLowerInvariant()}" });
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add(new[] { $"versions:{gameVersion}" });
        var url = $"search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}&limit=30&index=relevance";
        using var response = await Client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return json.RootElement.GetProperty("hits").EnumerateArray().Select(hit => new ModrinthProject(
            hit.GetProperty("project_id").GetString() ?? "", hit.GetProperty("slug").GetString() ?? "",
            hit.GetProperty("title").GetString() ?? "Untitled", hit.GetProperty("description").GetString() ?? "",
            hit.GetProperty("downloads").GetInt64(), hit.GetProperty("project_type").GetString() ?? "mod")).ToArray();
    }

    public async Task<ModrinthInstallResult> InstallAsync(ModrinthProject project, string projectType, string loader,
        string gameVersion, string instancesRoot, CancellationToken cancellationToken = default)
    {
        if (projectType is not ("mod" or "modpack" or "resourcepack" or "shader")) throw new ArgumentOutOfRangeException(nameof(projectType));
        if (projectType == "mod" && loader is "Vanilla" or "Any")
            throw new InvalidOperationException("Для модов выберите Fabric, Forge, NeoForge или Quilt. Сначала установите профиль с этим загрузчиком.");

        var instance = Path.Combine(instancesRoot, SafeSegment($"{gameVersion}-{loader.ToLowerInvariant()}"));
        var folder = projectType switch
        {
            "mod" => "mods", "resourcepack" => "resourcepacks", "shader" => "shaderpacks", _ => "modpacks"
        };
        var target = Path.Combine(instance, folder);
        Directory.CreateDirectory(target);
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new List<string>();
        var primary = await InstallProjectAsync(project.ProjectId, null, projectType, loader, gameVersion, target, installed, dependencies, cancellationToken);

        if (projectType == "modpack" && primary.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            var downloadedPack = Path.Combine(target, primary);
            await ExtractModpackAsync(downloadedPack, instance, cancellationToken);
        }

        return new ModrinthInstallResult(primary, dependencies, target);
    }

    public static async Task ExtractModpackAsync(string mrpackPath, string instanceRoot, CancellationToken ct = default)
    {
        if (!File.Exists(mrpackPath)) return;

        using var archive = ZipFile.OpenRead(mrpackPath);
        var indexEntry = archive.GetEntry("modrinth.index.json");
        if (indexEntry == null) return;

        using var indexStream = indexEntry.Open();
        using var doc = await JsonDocument.ParseAsync(indexStream, cancellationToken: ct);
        var root = doc.RootElement;

        // 1. Extract overrides
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.Name))
            {
                var relative = entry.FullName["overrides/".Length..];
                var destPath = Path.Combine(instanceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }

        // 2. Download files listed in manifest
        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in files.EnumerateArray())
            {
                var path = item.GetProperty("path").GetString();
                if (string.IsNullOrWhiteSpace(path)) continue;

                var downloads = item.GetProperty("downloads").EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
                if (downloads.Length == 0) continue;

                var fileDest = Path.Combine(instanceRoot, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fileDest)!);

                if (!File.Exists(fileDest))
                {
                    var url = downloads[0]!;
                    using var res = await Client.GetAsync(url, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        await using var src = await res.Content.ReadAsStreamAsync(ct);
                        await using var dst = File.Create(fileDest);
                        await src.CopyToAsync(dst, ct);
                    }
                }
            }
        }
    }

    private static async Task<string> InstallProjectAsync(string projectId, string? exactVersionId, string projectType,
        string loader, string gameVersion, string target, HashSet<string> installed, List<string> dependencies, CancellationToken ct)
    {
        if (!installed.Add(projectId)) return projectId;
        var versions = await GetVersionsAsync(projectId, exactVersionId, projectType == "mod" ? loader : null, gameVersion, ct);
        var version = versions.FirstOrDefault();
        if (version.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"Для {projectId} нет версии под Minecraft {gameVersion} / {loader}.");
        if (projectType == "mod" && exactVersionId is not null)
        {
            var games = version.GetProperty("game_versions").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var loaders = version.GetProperty("loaders").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!games.Contains(gameVersion) || (loader is not ("Any" or "vanilla") && !loaders.Contains(loader.ToLowerInvariant())))
                throw new InvalidOperationException($"Обязательная версия зависимости несовместима с Minecraft {gameVersion} / {loader}.");
        }

        if (projectType == "mod")
        {
            foreach (var dep in version.GetProperty("dependencies").EnumerateArray())
            {
                var type = dep.GetProperty("dependency_type").GetString();
                var dependencyId = dep.TryGetProperty("project_id", out var project) ? project.GetString() : null;
                var dependencyVersion = dep.TryGetProperty("version_id", out var exact) ? exact.GetString() : null;
                if (type != "required") continue;
                var identity = !string.IsNullOrWhiteSpace(dependencyId) ? dependencyId : dependencyVersion;
                if (string.IsNullOrWhiteSpace(identity)) continue;
                dependencies.Add(identity);
                await InstallProjectAsync(identity, dependencyVersion, "mod", loader, gameVersion, target, installed, dependencies, ct);
            }
        }

        var files = version.GetProperty("files").EnumerateArray().ToArray();
        var file = files.FirstOrDefault(f => f.TryGetProperty("primary", out var primaryFlag) && primaryFlag.GetBoolean());
        if (file.ValueKind == JsonValueKind.Undefined && files.Length > 0) file = files[0];
        if (file.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Modrinth не вернул файл версии.");
        var name = SafeFileName(file.GetProperty("filename").GetString() ?? "download.bin");
        var url = file.GetProperty("url").GetString() ?? throw new InvalidOperationException("В ответе нет URL файла.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps || downloadUri.Host != "cdn.modrinth.com")
            throw new InvalidOperationException("Отклонён небезопасный URL загрузки.");
        using var response = await Client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps || finalUri.Host != "cdn.modrinth.com")
            throw new InvalidOperationException("Modrinth перенаправил загрузку на неизвестный источник.");
        if (response.Content.Headers.ContentLength is > MaxDownloadBytes) throw new InvalidOperationException("Файл превышает лимит 512 МБ.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var temp = Path.Combine(target, $".{Guid.NewGuid():N}.download");
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes) throw new InvalidOperationException("Файл превышает лимит 512 МБ.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            if (file.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("sha512", out var sha512))
            {
                await using var verify = File.OpenRead(temp);
                var actual = Convert.ToHexString(await SHA512.HashDataAsync(verify, ct)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(sha512.GetString() ?? "")))
                    throw new InvalidDataException("Хэш скачанного файла не совпал с Modrinth.");
            }
            var destination = Path.Combine(target, name);
            File.Move(temp, destination, overwrite: true);
            return name;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static async Task<JsonElement[]> GetVersionsAsync(string projectId, string? exactVersionId, string? loader, string gameVersion, CancellationToken ct)
    {
        var query = $"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}";
        if (!string.IsNullOrWhiteSpace(loader) && loader is not ("Any" or "vanilla"))
            query += $"&loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.ToLowerInvariant() }))}";
        var route = exactVersionId is null
            ? $"project/{Uri.EscapeDataString(projectId)}/version?{query}"
            : $"version/{Uri.EscapeDataString(exactVersionId)}";
        using var response = await Client.GetAsync(route, ct); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray()
            : [json.RootElement.Clone()];
    }

    private static string SafeSegment(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c));
    private static string SafeFileName(string value)
    {
        var name = Path.GetFileName(value.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") throw new InvalidDataException("Некорректное имя файла от каталога.");
        return name;
    }
}
