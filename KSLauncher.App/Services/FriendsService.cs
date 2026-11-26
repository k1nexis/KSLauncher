using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KSLauncher.App.Services;

public sealed record FriendModInfo(
    string Slug,
    string Title,
    string? ProjectId = null
);

public sealed record FriendModpackInfo(
    string ProjectId,
    string Slug,
    string Title,
    string Version,
    string Loader
);

public sealed class FriendPresence
{
    public bool IsOnline { get; set; }
    public bool IsPlaying { get; set; }
    public string? ServerName { get; set; }
    public string? ServerIp { get; set; }
    public int ServerPort { get; set; } = 25565;
    public string? GameVersion { get; set; }
    public string? Loader { get; set; } = "vanilla";
    public List<FriendModInfo> Mods { get; set; } = new();
    public FriendModpackInfo? Modpack { get; set; }
    public bool IsLocalHostedServer { get; set; }

    public bool HasMods => Mods.Count > 0;
    public bool HasModpack => Modpack != null;
    public bool CanJoinServer => IsOnline && !string.IsNullOrWhiteSpace(ServerIp);
}

public sealed class FriendProfile
{
    public string FriendId { get; set; } = "";
    public string Username { get; set; } = "";
    public string AccountSystem { get; set; } = "Offline";
    public bool IsTestFriend { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public FriendPresence Presence { get; set; } = new();

    public string SystemBadge => AccountSystem switch
    {
        "KS System" => "KS",
        "Microsoft" => "MS",
        "Ely.by" => "ELY",
        _ => "OFFLINE"
    };

    public string StatusText
    {
        get
        {
            if (!Presence.IsOnline) return "Не в сети";
            if (!Presence.IsPlaying) return "В сети (в лаунчере)";
            if (Presence.HasModpack)
                return $"Играет в сборку «{Presence.Modpack!.Title}» ({Presence.GameVersion})";
            if (Presence.HasMods)
                return $"На сервере {Presence.ServerName ?? Presence.ServerIp} • {Presence.GameVersion} ({Presence.Loader}, {Presence.Mods.Count} мод.)";
            if (!string.IsNullOrWhiteSpace(Presence.ServerIp))
                return $"На сервере {Presence.ServerName ?? Presence.ServerIp} • {Presence.GameVersion} ({Presence.Loader})";
            return $"Играет в Minecraft {Presence.GameVersion} ({Presence.Loader})";
        }
    }

    public string StatusColor => !Presence.IsOnline ? "#6B7B73" : (Presence.IsPlaying ? "#34D399" : "#38BDF8");
}

public sealed class FriendInvite
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string FromFriendId { get; set; } = "";
    public string FromUsername { get; set; } = "";
    public string FromAccountSystem { get; set; } = "Offline";
    public string ServerName { get; set; } = "";
    public string ServerIp { get; set; } = "";
    public int ServerPort { get; set; } = 25565;
    public string GameVersion { get; set; } = "1.20.4";
    public string Loader { get; set; } = "fabric";
    public List<FriendModInfo> Mods { get; set; } = new();
    public FriendModpackInfo? Modpack { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public string Description => Modpack != null
        ? $"{FromUsername} ({FromAccountSystem}) зовёт вас в сборку «{Modpack.Title}» ({GameVersion} • {Loader}) на сервер {ServerIp}:{ServerPort}"
        : $"{FromUsername} ({FromAccountSystem}) зовёт вас на сервер {ServerIp}:{ServerPort} ({GameVersion} • {Loader})";
}

internal sealed class FriendsStoreData
{
    public string MyFriendCode { get; set; } = "";
    public List<FriendProfile> Friends { get; set; } = new();
}

internal sealed class LanPacket
{
    public string Kind { get; set; } = "presence"; // "presence" | "invite"
    public string FriendId { get; set; } = "";
    public string Username { get; set; } = "";
    public string AccountSystem { get; set; } = "";
    public string? TargetFriendId { get; set; }
    public FriendPresence? Presence { get; set; }
    public FriendInvite? Invite { get; set; }
}

public sealed class FriendsService : IDisposable
{
    private const int LanPort = 25577;
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private FriendsStoreData _data = new();
    private UdpClient? _udpListener;
    private CancellationTokenSource? _lanCts;
    private readonly List<FriendInvite> _pendingInvites = new();

    public event Action? FriendsChanged;
    public event Action<FriendInvite>? InviteReceived;

    public string MyFriendCode => _data.MyFriendCode;
    public IReadOnlyList<FriendProfile> Friends => _data.Friends;
    public IReadOnlyList<FriendInvite> PendingInvites => _pendingInvites;

    public FriendsService()
    {
        _filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "ks-friends.json");
        Load();
    }

    private void Load()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<FriendsStoreData>(File.ReadAllText(_filePath));
                if (loaded != null)
                {
                    _data = loaded;
                }
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(_data.MyFriendCode) || !IsEightDigitKsCode(_data.MyFriendCode))
        {
            _data.MyFriendCode = GenerateFriendCode();
            Save();
        }

        if (_data.Friends.RemoveAll(f => f.IsTestFriend) > 0)
        {
            Save();
        }
    }

    private static bool IsEightDigitKsCode(string code)
    {
        if (!code.StartsWith("KS-", StringComparison.OrdinalIgnoreCase)) return false;
        var digits = code[3..];
        return digits.Length == 8 && digits.All(char.IsDigit);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, JsonOptions));
        }
        catch { }
    }

    private static string GenerateFriendCode()
    {
        var num = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000);
        return $"KS-{num}";
    }

    public FriendProfile AddFriend(string identifier)
    {
        var raw = identifier.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Введите ник или ID друга (например KS-12345678).");

        string username = raw;
        string friendId;
        string system = "Любая";

        if (raw.StartsWith("KS-", StringComparison.OrdinalIgnoreCase) && raw[3..].All(char.IsDigit) && raw.Length == 11)
        {
            friendId = raw.ToUpperInvariant();
            username = $"Игрок {raw[3..]}";
        }
        else if (raw.Length == 8 && raw.All(char.IsDigit))
        {
            friendId = $"KS-{raw}";
            username = $"Игрок {raw}";
        }
        else
        {
            if (raw.EndsWith(".ms", StringComparison.OrdinalIgnoreCase)) { username = raw[..^3]; system = "Microsoft"; }
            else if (raw.EndsWith(".ely", StringComparison.OrdinalIgnoreCase)) { username = raw[..^4]; system = "Ely.by"; }
            else if (raw.EndsWith(".ks", StringComparison.OrdinalIgnoreCase)) { username = raw[..^3]; system = "KS System"; }
            else if (raw.EndsWith(".offline", StringComparison.OrdinalIgnoreCase)) { username = raw[..^8]; system = "Offline"; }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(username.ToLowerInvariant()));
            var num = (BitConverter.ToUInt32(hash, 0) % 90_000_000) + 10_000_000;
            friendId = $"KS-{num}";
        }

        if (_data.Friends.Any(f => f.FriendId.Equals(friendId, StringComparison.OrdinalIgnoreCase)
                                  || f.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Друг {username} ({friendId}) уже добавлен в список.");
        }

        var friend = new FriendProfile
        {
            FriendId = friendId,
            Username = username,
            AccountSystem = system,
            IsTestFriend = false,
            AddedAt = DateTime.UtcNow,
            Presence = new FriendPresence { IsOnline = false }
        };

        _data.Friends.Insert(0, friend);
        Save();
        FriendsChanged?.Invoke();
        return friend;
    }

    public static string EncodeInviteCode(FriendInvite invite)
    {
        var json = JsonSerializer.Serialize(invite);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"KSINV:{b64}";
    }

    public static FriendInvite? TryDecodeInviteCode(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("KSINV:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var b64 = trimmed[6..].Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
            }
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return JsonSerializer.Deserialize<FriendInvite>(json);
        }
        catch { return null; }
    }

    public void RemoveFriend(string friendId)
    {
        _data.Friends.RemoveAll(f => f.FriendId.Equals(friendId, StringComparison.OrdinalIgnoreCase));
        Save();
        FriendsChanged?.Invoke();
    }

    public void DismissInvite(string inviteId)
    {
        _pendingInvites.RemoveAll(i => i.Id == inviteId);
        FriendsChanged?.Invoke();
    }

    public void AddIncomingInvite(FriendInvite invite)
    {
        _pendingInvites.Insert(0, invite);
        InviteReceived?.Invoke(invite);
        FriendsChanged?.Invoke();
    }

    public void StartLanSync(Func<(string Username, string AccountSystem, FriendPresence Presence)> getMyState)
    {
        if (_lanCts != null) return;
        _lanCts = new CancellationTokenSource();
        var token = _lanCts.Token;

        // Listener
        _ = Task.Run(async () =>
        {
            try
            {
                _udpListener = new UdpClient();
                _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, LanPort));

                while (!token.IsCancellationRequested)
                {
                    var result = await _udpListener.ReceiveAsync(token);
                    var json = Encoding.UTF8.GetString(result.Buffer);
                    var packet = JsonSerializer.Deserialize<LanPacket>(json);
                    if (packet == null || packet.FriendId == _data.MyFriendCode) continue;

                    if (packet.Kind == "presence" && packet.Presence != null)
                    {
                        var existing = _data.Friends.FirstOrDefault(f =>
                            f.FriendId.Equals(packet.FriendId, StringComparison.OrdinalIgnoreCase) ||
                            f.Username.Equals(packet.Username, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Presence = packet.Presence;
                            existing.AccountSystem = packet.AccountSystem;
                            FriendsChanged?.Invoke();
                        }
                    }
                    else if (packet.Kind == "invite" && packet.Invite != null)
                    {
                        if (string.IsNullOrWhiteSpace(packet.TargetFriendId) || packet.TargetFriendId == _data.MyFriendCode)
                        {
                            _pendingInvites.Insert(0, packet.Invite);
                            InviteReceived?.Invoke(packet.Invite);
                            FriendsChanged?.Invoke();
                        }
                    }
                }
            }
            catch { }
        }, token);

        // Broadcaster
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var state = getMyState();
                    var packet = new LanPacket
                    {
                        Kind = "presence",
                        FriendId = _data.MyFriendCode,
                        Username = state.Username,
                        AccountSystem = state.AccountSystem,
                        Presence = state.Presence
                    };
                    BroadcastPacket(packet);
                }
                catch { }
                try { await Task.Delay(5000, token); } catch { break; }
            }
        }, token);
    }

    public void SendInvite(FriendProfile? target, string myUsername, string mySystem, FriendPresence myPresence)
    {
        var invite = new FriendInvite
        {
            FromFriendId = _data.MyFriendCode,
            FromUsername = myUsername,
            FromAccountSystem = mySystem,
            ServerName = myPresence.ServerName ?? $"Сервер {myUsername}",
            ServerIp = myPresence.ServerIp ?? GetLocalLanIp(),
            ServerPort = myPresence.ServerPort,
            GameVersion = myPresence.GameVersion ?? "1.20.4",
            Loader = myPresence.Loader ?? "vanilla",
            Mods = myPresence.Mods,
            Modpack = myPresence.Modpack
        };

        var packet = new LanPacket
        {
            Kind = "invite",
            FriendId = _data.MyFriendCode,
            Username = myUsername,
            AccountSystem = mySystem,
            TargetFriendId = target?.FriendId,
            Invite = invite
        };

        BroadcastPacket(packet);
    }

    private static void BroadcastPacket(LanPacket packet)
    {
        try
        {
            using var client = new UdpClient { EnableBroadcast = true };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
            client.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, LanPort));
        }
        catch { }
    }

    public static string GetLocalLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
                return endPoint.Address.ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    public void Dispose()
    {
        try { _lanCts?.Cancel(); } catch { }
        try { _udpListener?.Close(); } catch { }
        try { _udpListener?.Dispose(); } catch { }
    }
}
