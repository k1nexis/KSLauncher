using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KSLauncher.App.Services;

public sealed class UpnpPortForwarder
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<string?> GetPublicIpAsync(CancellationToken ct = default)
    {
        string[] endpoints =
        [
            "https://api.ipify.org",
            "https://checkip.amazonaws.com",
            "https://ifconfig.me/ip"
        ];
        foreach (var url in endpoints)
        {
            try
            {
                var ip = (await Http.GetStringAsync(url, ct)).Trim();
                if (IPAddress.TryParse(ip, out _)) return ip;
            }
            catch { }
        }
        return null;
    }

    public static async Task<(bool Success, string Message)> TryForwardPortAsync(int port, string description = "KSLauncher Minecraft Server", CancellationToken ct = default)
    {
        try
        {
            var localIp = FriendsService.GetLocalLanIp();
            if (string.IsNullOrWhiteSpace(localIp) || localIp == "127.0.0.1")
                return (false, "Не определён локальный IP");

            var location = await DiscoverIgdLocationAsync(ct);
            if (string.IsNullOrWhiteSpace(location))
                return (false, "Роутер не отвечает на UPnP (откройте порт вручную или используйте Radmin/Playit)");

            var xml = await Http.GetStringAsync(location, ct);
            var doc = XDocument.Parse(xml);
            var controlUrl = FindControlUrl(doc, location);
            if (string.IsNullOrWhiteSpace(controlUrl))
                return (false, "Служба UPnP WANIPConnection не найдена");

            var serviceType = xml.Contains("WANPPPConnection:1", StringComparison.OrdinalIgnoreCase)
                ? "urn:schemas-upnp-org:service:WANPPPConnection:1"
                : "urn:schemas-upnp-org:service:WANIPConnection:1";

            var soap = $"""
                <?xml version="1.0"?>
                <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                  <s:Body>
                    <u:AddPortMapping xmlns:u="{serviceType}">
                      <NewRemoteHost></NewRemoteHost>
                      <NewExternalPort>{port}</NewExternalPort>
                      <NewProtocol>TCP</NewProtocol>
                      <NewInternalPort>{port}</NewInternalPort>
                      <NewInternalClient>{localIp}</NewInternalClient>
                      <NewEnabled>1</NewEnabled>
                      <NewPortMappingDescription>{System.Security.SecurityElement.Escape(description)}</NewPortMappingDescription>
                      <NewLeaseDuration>0</NewLeaseDuration>
                    </u:AddPortMapping>
                  </s:Body>
                </s:Envelope>
                """;

            using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
            req.Headers.Add("SOAPAction", $"\"{serviceType}#AddPortMapping\"");
            req.Content = new StringContent(soap, Encoding.UTF8, "text/xml");

            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return (true, $"Порт {port} открыт через UPnP");

            return (false, $"UPnP отклонил запрос (HTTP {(int)resp.StatusCode})");
        }
        catch
        {
            return (false, "UPnP недоступен на текущем роутере");
        }
    }

    private static async Task<string?> DiscoverIgdLocationAsync(CancellationToken ct)
    {
        const string searchMsg =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 2500;
        var bytes = Encoding.ASCII.GetBytes(searchMsg);
        await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(2500);
        try
        {
            var res = await udp.ReceiveAsync(timeoutCts.Token);
            var text = Encoding.ASCII.GetString(res.Buffer);
            var match = Regex.Match(text, @"LOCATION:\s*(.+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindControlUrl(XDocument doc, string baseLocation)
    {
        foreach (var service in doc.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value ?? "";
            if (type.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase))
            {
                var ctrl = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
                if (!string.IsNullOrWhiteSpace(ctrl))
                {
                    return new Uri(new Uri(baseLocation), ctrl).ToString();
                }
            }
        }
        return null;
    }
}
