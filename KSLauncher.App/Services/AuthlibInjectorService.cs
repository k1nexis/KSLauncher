using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace KSLauncher.App.Services;

/// <summary>Fetches the injector from the GitHub repository linked by Ely.by's own docs.</summary>
public sealed class AuthlibInjectorService
{
    private const string ReleaseApi = "https://api.github.com/repos/yushijinhun/authlib-injector/releases/latest";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    public async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "libraries");
        var path = Path.Combine(directory, "authlib-injector.jar");
        await DownloadLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 100_000) return path;
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KSLauncher", "0.1"));
            using var releaseResponse = await Client.SendAsync(request, cancellationToken);
            releaseResponse.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await releaseResponse.Content.ReadAsStringAsync(cancellationToken));
            var asset = release.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(x => x.GetProperty("name").GetString()?.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) == true);
            if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("В последнем релизе authlib-injector не найден JAR-файл.");
            var assetUrl = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/yushijinhun/authlib-injector/releases/download/", StringComparison.Ordinal))
                throw new InvalidOperationException("Источник authlib-injector не совпал с официальным репозиторием.");
            using var assetResponse = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            assetResponse.EnsureSuccessStatusCode();
            await using var download = await assetResponse.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new MemoryStream();
            await download.CopyToAsync(output, cancellationToken);
            if (output.Length is < 100_000 or > 20_000_000) throw new InvalidOperationException("Размер authlib-injector.jar выглядит некорректно.");
            var bytes = output.ToArray();
            if (asset.TryGetProperty("digest", out var digestValue) && digestValue.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var actual = Convert.ToHexString(SHA256.HashData(bytes));
                if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Контрольная сумма authlib-injector не совпала.");
            }
            Directory.CreateDirectory(directory);
            var temp = path + ".download";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        finally { DownloadLock.Release(); }
    }
}
