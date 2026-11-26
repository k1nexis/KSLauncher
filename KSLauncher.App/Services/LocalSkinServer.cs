using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KSLauncher.App.Services;

/// <summary>
/// Lightweight local Yggdrasil skin server for authlib-injector so KS System & Offline skins
/// render in both singleplayer and multiplayer, with Mojang & Ely.by fallback for other players.
/// </summary>
public sealed class LocalSkinServer : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public static string CosmeticsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KSLauncher", "ks-cosmetics");

    public static void SaveLocalCosmetic(string username, string kind, byte[] pngBytes)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var dir = Path.Combine(CosmeticsRoot, username.ToLowerInvariant());
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{kind}.png"), pngBytes);
    }

    public static string? GetLocalCosmeticPath(string username, string kind)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var path = Path.Combine(CosmeticsRoot, username.ToLowerInvariant(), $"{kind}.png");
        return File.Exists(path) ? path : null;
    }

    public void EnsureStarted()
    {
        if (_listener != null && _listener.IsListening) return;

        Port = FindFreePort();
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();

        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private static int FindFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";

            // 1. Root metadata for authlib-injector
            if (path == "/" || path == "")
            {
                var meta = new
                {
                    meta = new
                    {
                        serverName = "KS System",
                        implementationName = "KSLauncher-Yggdrasil",
                        implementationVersion = "0.2.0"
                    },
                    skinDomains = new[] { "127.0.0.1", "localhost", ".mojang.com", ".minecraft.net", ".ely.by" }
                };
                await WriteJsonAsync(ctx, 200, meta);
                return;
            }

            // 2. Texture PNG serving: /textures/{kind}/{username}
            if (path.StartsWith("/textures/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var kind = parts[1].ToLowerInvariant();
                    var username = Uri.UnescapeDataString(parts[2]);
                    var file = GetLocalCosmeticPath(username, kind);
                    if (file != null)
                    {
                        var bytes = await File.ReadAllBytesAsync(file);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "image/png";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                        ctx.Response.Close();
                        return;
                    }
                }
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            // 3. Profile lookup by UUID: /sessionserver/session/minecraft/profile/{uuid}
            if (path.StartsWith("/sessionserver/session/minecraft/profile/", StringComparison.OrdinalIgnoreCase))
            {
                var uuid = path["/sessionserver/session/minecraft/profile/".Length..].TrimEnd('/');
                var matchedUser = FindUserByOfflineUuid(uuid);
                if (matchedUser != null && HasAnyLocalCosmetic(matchedUser))
                {
                    await WriteJsonAsync(ctx, 200, BuildYggdrasilProfile(uuid, matchedUser));
                    return;
                }

                // Fallback to Mojang sessionserver for licensed players
                try
                {
                    var mojangJson = await Http.GetStringAsync($"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}");
                    await WriteRawJsonAsync(ctx, 200, mojangJson);
                    return;
                }
                catch { }

                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            // 4. Bulk profile lookup by username: /api/profiles/minecraft
            if (path.Equals("/api/profiles/minecraft", StringComparison.OrdinalIgnoreCase) && ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var names = JsonSerializer.Deserialize<string[]>(body) ?? [];
                var result = new List<object>();
                foreach (var name in names)
                {
                    if (HasAnyLocalCosmetic(name))
                    {
                        result.Add(new { id = ComputeOfflineUuid(name), name });
                    }
                }
                await WriteJsonAsync(ctx, 200, result);
                return;
            }

            // 5. hasJoined fallback (for local/offline servers)
            if (path.StartsWith("/sessionserver/session/minecraft/hasJoined", StringComparison.OrdinalIgnoreCase))
            {
                var username = ctx.Request.QueryString["username"];
                if (!string.IsNullOrWhiteSpace(username) && HasAnyLocalCosmetic(username))
                {
                    await WriteJsonAsync(ctx, 200, BuildYggdrasilProfile(ComputeOfflineUuid(username), username));
                    return;
                }
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private object BuildYggdrasilProfile(string uuid, string username)
    {
        var textures = new Dictionary<string, object>();
        if (GetLocalCosmeticPath(username, "skin") != null)
        {
            textures["SKIN"] = new { url = $"{BaseUrl}textures/skin/{Uri.EscapeDataString(username)}" };
        }
        if (GetLocalCosmeticPath(username, "cape") != null)
        {
            textures["CAPE"] = new { url = $"{BaseUrl}textures/cape/{Uri.EscapeDataString(username)}" };
        }

        var payload = new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            profileId = uuid,
            profileName = username,
            textures
        };
        var payloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));

        return new
        {
            id = uuid,
            name = username,
            properties = new[]
            {
                new { name = "textures", value = payloadB64 }
            }
        };
    }

    private static bool HasAnyLocalCosmetic(string username) =>
        GetLocalCosmeticPath(username, "skin") != null || GetLocalCosmeticPath(username, "cape") != null;

    private static string? FindUserByOfflineUuid(string uuid)
    {
        if (!Directory.Exists(CosmeticsRoot)) return null;
        var cleanUuid = uuid.Replace("-", "").ToLowerInvariant();
        foreach (var dir in Directory.GetDirectories(CosmeticsRoot))
        {
            var name = Path.GetFileName(dir);
            if (ComputeOfflineUuid(name) == cleanUuid)
                return name;
        }
        return null;
    }

    public static string ComputeOfflineUuid(string username)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{username}"));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object data)
    {
        await WriteRawJsonAsync(ctx, status, JsonSerializer.Serialize(data));
    }

    private static async Task WriteRawJsonAsync(HttpListenerContext ctx, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
    }
}
