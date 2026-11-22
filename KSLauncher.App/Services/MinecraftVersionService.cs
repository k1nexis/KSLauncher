using System.Net.Http.Json;
using System.Net.Http;

namespace KSLauncher.App.Services;

public sealed record MinecraftVersion(string Id, string Type);

/// <summary>Official Mojang launcher metadata; returns all versions, newest first.</summary>
public sealed class MinecraftVersionService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    public async Task<IReadOnlyList<MinecraftVersion>> GetVersionsAsync(bool includeSnapshots = false, CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(ManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var manifest = await response.Content.ReadFromJsonAsync<VersionManifest>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Список версий Minecraft пуст.");
        return manifest.Versions
            .Where(v => includeSnapshots || v.Type == "release")
            .Select(v => new MinecraftVersion(v.Id, v.Type))
            .ToArray();
    }

    private sealed record VersionManifest(List<VersionEntry> Versions);
    private sealed record VersionEntry(string Id, string Type);
}
