using System.IO;
using System.Text.Json;

namespace KSLauncher.App.Services;

public sealed record SavedAccount(
    string Id,
    string Username,
    string Type,
    string? Uuid = null,
    string? AccessToken = null,
    long ExpiresAt = 0,
    bool IsActive = false
);

public sealed class AccountManager
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AccountManager()
    {
        _filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSLauncher", "saved-accounts.json");
    }

    public List<SavedAccount> GetAccounts()
    {
        if (!File.Exists(_filePath)) return new List<SavedAccount>();
        try
        {
            var list = JsonSerializer.Deserialize<List<SavedAccount>>(File.ReadAllText(_filePath));
            return list ?? new List<SavedAccount>();
        }
        catch { return new List<SavedAccount>(); }
    }

    public void SaveAccount(SavedAccount account)
    {
        var accounts = GetAccounts();
        accounts.RemoveAll(x => x.Id.Equals(account.Id, StringComparison.OrdinalIgnoreCase) || (x.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase) && x.Type == account.Type));
        // Reset other actives if this one is active
        if (account.IsActive)
        {
            for (int i = 0; i < accounts.Count; i++)
            {
                accounts[i] = accounts[i] with { IsActive = false };
            }
        }
        accounts.Insert(0, account);
        Persist(accounts);
    }

    public void SetActiveAccount(string id)
    {
        var accounts = GetAccounts();
        for (int i = 0; i < accounts.Count; i++)
        {
            accounts[i] = accounts[i] with { IsActive = accounts[i].Id.Equals(id, StringComparison.OrdinalIgnoreCase) };
        }
        Persist(accounts);
    }

    public SavedAccount? GetActiveAccount()
    {
        var accounts = GetAccounts();
        return accounts.FirstOrDefault(x => x.IsActive) ?? accounts.FirstOrDefault();
    }

    public void RemoveAccount(string id)
    {
        var accounts = GetAccounts();
        accounts.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (accounts.Count > 0 && !accounts.Any(x => x.IsActive))
        {
            accounts[0] = accounts[0] with { IsActive = true };
        }
        Persist(accounts);
    }

    private void Persist(List<SavedAccount> accounts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(accounts, JsonOptions));
    }
}
