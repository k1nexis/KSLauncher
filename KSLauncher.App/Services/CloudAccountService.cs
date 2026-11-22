using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Text.Json;

namespace KSLauncher.App.Services;

public sealed class CloudAccountService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public string? AccessToken { get; private set; }
    public string? Username { get; private set; }

    public async Task<string> LoginAsync(string baseUrl, string email, string password, CancellationToken cancellationToken = default)
        => await AuthenticateAsync(baseUrl, "/v1/auth/login", new { email, password }, cancellationToken);

    public async Task<string> RegisterAsync(string baseUrl, string email, string username, string password, CancellationToken cancellationToken = default)
        => await AuthenticateAsync(baseUrl, "/v1/auth/register", new { email, username, password }, cancellationToken);

    private async Task<string> AuthenticateAsync<T>(string baseUrl, string path, T payload, CancellationToken cancellationToken)
    {
        var root = NormalizeBaseUrl(baseUrl);
        using var response = await Client.PostAsJsonAsync(new Uri(root, path), payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : $"Cloud API вернул HTTP {(int)response.StatusCode}.");
        AccessToken = json.RootElement.GetProperty("accessToken").GetString();
        Username = json.RootElement.GetProperty("account").GetProperty("username").GetString();
        if (string.IsNullOrWhiteSpace(AccessToken) || string.IsNullOrWhiteSpace(Username))
            throw new InvalidOperationException("Cloud API вернул неполный ответ аккаунта.");
        return Username!;
    }

    public async Task UploadPngAsync(string baseUrl, string kind, string filePath, CancellationToken cancellationToken = default)
    {
        if (kind is not ("skin" or "cape")) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(AccessToken)) throw new InvalidOperationException("Сначала войдите в KS System.");
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length is < 8 or > 4 * 1024 * 1024) throw new InvalidOperationException("Выберите PNG размером до 4 МБ.");
        await using var input = File.OpenRead(filePath);
        var signature = new byte[PngSignature.Length];
        var read = await input.ReadAsync(signature, cancellationToken);
        if (read != PngSignature.Length || !signature.SequenceEqual(PngSignature)) throw new InvalidOperationException("Файл не является PNG.");
        input.Position = 0;
        using var content = new StreamContent(input);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(NormalizeBaseUrl(baseUrl), $"/v1/cosmetics/{kind}")) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await Client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Не удалось загрузить косметику (HTTP {(int)response.StatusCode}): {body}");
        }
    }

    public async Task LogoutAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(AccessToken)) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(NormalizeBaseUrl(baseUrl), "/v1/auth/logout"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        AccessToken = null;
        Username = null;
    }

    private static Uri NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new ArgumentException("Укажите HTTPS URL API KS System.");
        return new Uri(uri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
    }
}
