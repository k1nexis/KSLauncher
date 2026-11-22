using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace KSLauncher.App.Services;

public sealed record ElyAccount(string Username, string Uuid, string AccessToken, long ExpiresAt);

/// <summary>OAuth handoff through the KS Worker; Ely client secrets never enter the desktop app.</summary>
public sealed class ElyAccountService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<ElyAccount> SignInAsync(string workerBaseUrl, CancellationToken cancellationToken = default)
    {
        var worker = NormalizeWorkerUrl(workerBaseUrl);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var returnUri = $"http://127.0.0.1:{port}/ely/callback";
            var startUrl = new Uri(worker, $"v1/ely/start?return_uri={Uri.EscapeDataString(returnUri)}");
            using var startResponse = await Client.GetAsync(startUrl, cancellationToken);
            var startBody = await startResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!startResponse.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(startBody, $"Worker вернул HTTP {(int)startResponse.StatusCode}."));
            using var startJson = JsonDocument.Parse(startBody);
            var authorizationUrl = startJson.RootElement.GetProperty("authorizationUrl").GetString();
            var expectedState = startJson.RootElement.GetProperty("state").GetString();
            if (!Uri.TryCreate(authorizationUrl, UriKind.Absolute, out var authUri) || authUri.Host != "account.ely.by" || string.IsNullOrWhiteSpace(expectedState))
                throw new InvalidOperationException("Worker вернул некорректный адрес Ely.by.");

            Process.Start(new ProcessStartInfo(authUri.AbsoluteUri) { UseShellExecute = true });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("Браузер не передал ответ Ely.by.");
            string? header;
            do { header = await reader.ReadLineAsync(timeout.Token); } while (!string.IsNullOrEmpty(header));
            var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length < 2 || !requestParts[1].StartsWith("/ely/callback?", StringComparison.Ordinal))
                throw new InvalidOperationException("Получен неожиданный локальный адрес возврата Ely.by.");
            var callback = new Uri("http://127.0.0.1" + requestParts[1]);
            var query = ParseQuery(callback.Query);
            var html = "<!doctype html><meta charset=utf-8><title>KS Launcher</title><p>Вход Ely.by завершён. Можно закрыть эту вкладку и вернуться в KS Launcher.</p>";
            var htmlBytes = System.Text.Encoding.UTF8.GetBytes(html);
            var responseHeader = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {htmlBytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(responseHeader), timeout.Token);
            await stream.WriteAsync(htmlBytes, timeout.Token);

            if (!query.TryGetValue("state", out var actualState) || actualState != expectedState)
                throw new InvalidOperationException("Проверка состояния OAuth не прошла.");
            if (query.TryGetValue("error", out var oauthError))
                throw new InvalidOperationException($"Ely.by отменил вход: {oauthError}.");
            if (!query.TryGetValue("ticket", out var ticket) || string.IsNullOrWhiteSpace(ticket))
                throw new InvalidOperationException("Ely.by не вернул одноразовый билет входа.");

            using var exchangeResponse = await Client.PostAsJsonAsync(new Uri(worker, "v1/ely/exchange"), new { ticket }, cancellationToken);
            var exchangeBody = await exchangeResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!exchangeResponse.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(exchangeBody, $"Не удалось получить сессию Ely.by (HTTP {(int)exchangeResponse.StatusCode})."));
            using var exchangeJson = JsonDocument.Parse(exchangeBody);
            var root = exchangeJson.RootElement;
            return new ElyAccount(root.GetProperty("username").GetString()!, root.GetProperty("uuid").GetString()!, root.GetProperty("accessToken").GetString()!, root.GetProperty("expiresAt").GetInt64());
        }
        finally { listener.Stop(); }
    }

    private static Uri NormalizeWorkerUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new ArgumentException("Для Ely.by укажи HTTPS URL Worker KS System.");
        return uri;
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2)
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);

    private static string ReadError(string body, string fallback)
    {
        try { using var json = JsonDocument.Parse(body); if (json.RootElement.TryGetProperty("error", out var error)) return error.GetString() ?? fallback; }
        catch (JsonException) { }
        return fallback;
    }
}
