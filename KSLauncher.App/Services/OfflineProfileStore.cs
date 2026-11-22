using System.Text.Json;
using System.Text.RegularExpressions;
using CmlLib.Core.Auth;
using System.IO;

namespace KSLauncher.App.Services;

public sealed record OfflineProfile(string Username, string Uuid);

public sealed class OfflineProfileStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "offline-profile.json");

    public async Task<OfflineProfile> SaveAsync(string username)
    {
        if (!Regex.IsMatch(username, "^[A-Za-z0-9_]{3,16}$"))
            throw new ArgumentException("Ник должен содержать 3–16 символов: латинские буквы, цифры и _.");

        // The offline UUID is deterministic for this nickname, matching the Minecraft offline profile convention.
        var session = MSession.CreateOfflineSession(username);
        var profile = new OfflineProfile(username, session.UUID ?? throw new InvalidOperationException("Не удалось создать UUID профиля."));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        return profile;
    }

    public async Task<OfflineProfile?> LoadAsync()
    {
        if (!File.Exists(_path)) return null;
        try { return JsonSerializer.Deserialize<OfflineProfile>(await File.ReadAllTextAsync(_path)); }
        catch (JsonException) { return null; }
    }
}
